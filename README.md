# LCapture

Visualizador e gravador de vídeo para Windows, feito para mostrar com baixa latência o sinal de uma
fonte de captura de vídeo USB (UVC), como uma placa de captura Fifine AmpliGame V3 ligada via HDMI.
Usa a API nativa `Windows.Media.Capture` para obter os frames diretamente da placa com a maior
qualidade/resolução nativa que ela anunciar, sem recompressão. Opcionalmente grava para MP4, com
uma webcam ou uma janela (por exemplo, o browser) no canto da imagem.

## Instalar

Disponível na Microsoft Store: https://apps.microsoft.com/detail/9MTK97FH4JQ0

## Requisitos

- Windows 10 (1809+) ou Windows 11.
- .NET 10 SDK para compilar (`winget install Microsoft.DotNet.SDK.10`). Quem usa o instalador
  não precisa de instalar o .NET.
- A placa de captura deve aparecer no Windows como uma câmera/dispositivo de vídeo (UVC padrão —
  é o caso da Fifine AmpliGame V3).

## Compilar e executar

```powershell
dotnet build LCapture.sln -c Release
dotnet run --project src/LCapture/LCapture.csproj -c Release
```

O executável fica em `src/LCapture/bin/x64/Release/net10.0-windows10.0.22621.0/LCapture.exe`.

Se o código estiver dentro do WSL, não abra o executável a partir de `\\wsl.localhost\...`: o
Windows não deixa apps em pastas de rede usar a câmera (erro `0x80070490 — Elemento não
encontrado`). Use `powershell -File tools\run-dev.ps1`, que compila, copia para
`%LOCALAPPDATA%\LCaptureDev` e abre de lá. O registo de diagnóstico fica em
`%LOCALAPPDATA%\LCapture\lcapture.log`.

## Gerar o instalador (para outros PCs)

```powershell
winget install JRSoftware.InnoSetup   # uma vez
powershell -File tools\publish.ps1
```

Gera `artifacts/publish/win-x64/` (versão autocontida, já inclui o runtime .NET) e
`installer/Output/LCapture-Setup-<versão>.exe`. O instalador não pede direitos de administrador,
cria atalho no Menu Iniciar e desinstalador. Funciona com qualquer placa de vídeo (NVIDIA, AMD,
Intel) e qualquer placa de captura UVC. Para não ser bloqueado pelo SmartScreen/Smart App Control
noutros PCs, o instalador e o `LCapture.exe` precisam de assinatura digital (ver secção abaixo).

## Uso

1. Conecte a placa de captura antes de abrir o app (ou clique em **Atualizar** depois de conectar).
2. Escolha a fonte em **Fonte de vídeo**. Na primeira vez o app escolhe sozinho: uma placa
   Fifine/AmpliGame se houver, senão um dispositivo externo (webcams integradas ficam por último).
   O áudio escolhido automaticamente é o da mesma placa de captura; se não for possível
   identificá-lo, nenhum áudio é ligado (para nunca reproduzir um microfone nas colunas).
3. Clique em **Iniciar**. O combo **Qualidade** lista todos os modos nativos suportados pela placa
   (resolução × taxa de quadros × formato); o de melhor qualidade já vem pré-selecionado.
4. **F11** ou duplo clique no vídeo: alterna tela cheia (o cursor some sozinho após alguns segundos
   parado). **Esc** sai da tela cheia. O botão de minimizar da janela funciona normalmente.
5. A última fonte/qualidade usada é lembrada e reconectada automaticamente na próxima abertura.
6. **Nitidez por GPU** (botão com ícone de sol, azul quando ligado): aplica um filtro de nitidez
   adaptativo ao contraste (estilo AMD CAS) direto na GPU, sem custo perceptível. Realça detalhes
   sem criar halos em bordas já contrastadas. Não é IA/DLSS — o DLSS depende de dados do motor de
   jogo e não se aplica a vídeo capturado. O estado ligado/desligado é lembrado. O slider
   "Nitidez" (canto direito da linha de status, ativo só com o filtro ligado) ajusta a intensidade
   de 0 a 100% e também é lembrado.
7. **Lista de qualidade enxuta**: a placa costuma anunciar 150+ combinações (resolução × fps ×
   formato de pixel). O app mostra só o melhor de cada resolução conhecida (HD, Full HD, 2K, 4K),
   com a maior taxa de quadros e preferindo formato sem compressão (MJPG só se não houver outro).
   Se a placa oferecer 1440p/2160p, essas opções aparecem automaticamente, e o padrão é o maior
   modo que cabe na sua tela com 50+ fps. (A Fifine testada anuncia no máximo 1080p60.)
   O shader fica em `src/LCapture/Shaders/Sharpen.hlsl`; para recompilar após editar:
   `powershell -File tools\compile-shader.ps1`.

## Gravação (F9)

- **Gravar** (ou **F9**) grava o que se vê — incluindo a sobreposição — para MP4 (H.264 + AAC), com
  o codificador de hardware da placa de vídeo (NVENC, AMD AMF, Intel Quick Sync) quando existe.
  Sem ele, a gravação usa o processador e o app avisa.
- **⚙ Definições da gravação**: resolução (igual à fonte, 2160p, 1440p, 1080p, 720p — só até à da
  fonte, mantendo a proporção), taxa de quadros (igual à fonte ou 30 fps), qualidade (Alta, Média,
  Baixa) com o tamanho estimado por hora, e a pasta (por omissão `Vídeos\LCapture`).
- O som gravado mistura o áudio da placa (com o volume e o mudo escolhidos: grava o que ouve), o
  microfone (se escolhido) e, no Windows 11, o som da janela capturada. Trocar de áudio ou de
  camadas não interrompe a gravação.

## Painel Áudio

O botão **Áudio** mostra/esconde o painel lateral, com um cartão por canal:

- **Placa de captura**: fonte, mudo, volume e medidor de nível. Toca nas colunas e entra na gravação
  com este volume.
- **Microfone**: fonte (por omissão "Nenhum"), mudo, volume (0–100%), medidor e "Gravar o
  microfone". Nunca toca nas colunas (evita eco); só entra na gravação. Só fica ligado com o painel
  aberto (para o medidor) ou durante uma gravação.
- A gravação termina sozinha (e o ficheiro fica bom) ao trocar a qualidade da fonte principal,
  ao desligar a placa, ao fechar o app ou com menos de 500 MB livres no disco. É um MP4 normal:
  se o PC desligar a meio da gravação, o ficheiro perde-se.

## Camadas (webcam, imagens, janela, ecrã)

O botão **Camadas** monta a cena:

- **Imagem principal**: a fonte de vídeo da barra (a placa) ou uma **janela/ecrã** da lista
  (janelas abertas e ecrãs inteiros).
- **Sobreposições**, sem limite, em **＋ Adicionar camada…**:
  - **Webcam** (pedida em ≤ 720p, de preferência MJPG, para poupar USB);
  - **Imagem** (PNG com transparência, JPG, BMP, GIF): logótipo, moldura, marca de água. É copiada
    para `%LOCALAPPDATA%\LCapture\Imagens`, por isso mover o original não estraga a cena. Uma
    imagem com a forma da imagem principal (ex.: 1920×1080) começa a ocupá-la toda;
  - **Vídeo animado** (WebM, MP4, MOV, MKV): em ciclo, sem som (molduras e logótipos animados).
    O Windows perde a transparência dos WebM (as zonas transparentes vêm brancas), por isso há
    **"Fundo a remover: Nenhum / Preto / Branco"** — os WebM começam em "Branco". Com "Branco",
    o branco do próprio desenho (ex.: texto branco) também fica parcialmente transparente.
    WebM precisa das "Extensões de Multimédia Web" e "Extensões de Vídeo VP9" (Microsoft Store);
  - a **janela/ecrã** escolhida, ou a **fonte da barra** (quando a principal é uma janela).
- **Na própria imagem**: clique numa camada para a selecionar, arraste para mover, puxe um canto
  para mudar o tamanho (mantém a forma; **Shift** = livre). As margens e o centro atraem a camada.
  **Delete** apaga a selecionada, **Esc** tira a seleção. A moldura azul de seleção não é gravada.
- **Botão direito** numa camada: **Ajustar ao ecrã** (o maior tamanho que cabe, centrado, sem
  deformar), **Esticar para ocupar tudo**, posição (cantos, centro), ordem, esconder e remover.
- Por camada: mostrar/esconder (numa webcam, o olho liga/desliga a câmera), trazer para a frente /
  enviar para trás, opacidade, posições rápidas (cantos, centro, ocupar tudo) e remover.
- **Acompanhar**: uma camada pode seguir outra (move-se e muda de tamanho com ela). Para uma
  moldura de webcam: adicione a imagem, em "Acompanhar" escolha a webcam e carregue em **Ajustar**.
- **Trocar com a imagem principal** (numa camada de janela ou da fonte da barra): troca, por
  exemplo, a consola e o browser, mantendo o lugar e o tamanho da camada.
- **Bloquear** impede arrastar as camadas sem querer (por exemplo, a meio de um jogo).
- A cena fica guardada: imagens e webcams voltam ao abrir a app, mas **as webcams voltam
  desligadas** (ligam-se no olho). Janelas não são guardadas.
- Som da janela: só a aplicação dessa janela (ou, num ecrã inteiro, tudo menos o LCapture), para
  não duplicar o som da placa. Requer Windows 11 (build 20348+).
- Limites do Windows: vídeo protegido (Netflix, Disney+…) aparece preto; uma janela minimizada fica
  parada. No Windows 11 a borda amarela de captura é escondida quando o Windows o permite.

## Painel Áudio

O botão **Áudio** mostra/esconde o painel lateral, com um cartão por canal:

- **Placa de captura**: fonte, mudo, volume e medidor de nível. Toca nas colunas e entra na gravação
  com este volume.
- **Microfone**: fonte (por omissão "Nenhum"), mudo, volume (0–100%), medidor e "Gravar o
  microfone". Nunca toca nas colunas (evita eco); só entra na gravação. Só fica ligado com o painel
  aberto (para o medidor) ou durante uma gravação.
- A gravação termina sozinha (e o ficheiro fica bom) ao trocar a qualidade da fonte principal,
  ao desligar a placa, ao fechar o app ou com menos de 500 MB livres no disco. É um MP4 normal:
  se o PC desligar a meio da gravação, o ficheiro perde-se.

## Camadas (webcam, janela, ecrã)

O botão **Camadas** (ícone de folhas) junta duas imagens:

- **Imagem principal**: a fonte de vídeo da barra (a placa) ou uma **janela/ecrã** da lista
  (janelas abertas e ecrãs inteiros).
- **Sobreposição no canto**: uma webcam (pedida em ≤ 720p, de preferência MJPG, para poupar USB),
  a janela/ecrã ou a placa; com posição (4 cantos) e tamanho (pequena, média, grande).
- **Trocar principal e sobreposição** troca, por exemplo, a consola e o browser com um clique.
- Som da janela: só a aplicação dessa janela (ou, num ecrã inteiro, tudo menos o LCapture), para
  não duplicar o som da placa. Requer Windows 11 (build 20348+). A app continua a tocar sozinha
  nas colunas; o LCapture só o junta à gravação.
- Limites do Windows: vídeo protegido (Netflix, Disney+…) aparece preto; uma janela minimizada fica
  parada. No Windows 11 a borda amarela de captura é escondida quando o Windows o permite.

## Teste do gravador (sem placa)

```powershell
dotnet run -c Release --project tests/RecordingSmokeTest -- 1920 1080 60
```

Grava 3 s de quadros sintéticos (vermelho em cima, azul em baixo, verde no canto) e um tom de
440 Hz, e lê o MP4 de volta. Esperado: `top=red bottom=blue overlay=green`, `dropped=0`,
`RMS≈0,212`, `~440 Hz`.

## Se o app fechar ao abrir ("Uma política de Controle de Aplicativos bloqueou este arquivo")

É o **Smart App Control** do Windows 11, que bloqueia binários sem assinatura digital (como o que
o `dotnet build` gera). Nas primeiras semanas ele fica em modo "avaliação" (não bloqueia) e depois
passa a bloquear. Soluções: assinar o `LCapture.dll`/`.exe` com um certificado de assinatura de
código publicamente confiável (certificado autoassinado NÃO satisfaz o Smart App Control), ou
desativar o Smart App Control em Segurança do Windows > Controle de aplicativos e navegador
(atenção: depois de desligado, só volta com reinstalação do Windows).

## Assinatura digital (para distribuir)

Sem assinatura, o SmartScreen avisa ao abrir o instalador e o Smart App Control do Windows 11
bloqueia o app. A opção mais barata é o **Azure Trusted Signing**:

1. Criar a conta Trusted Signing no portal Azure, validar a identidade e criar um perfil de
   certificado (Public Trust).
2. Instalar o Windows SDK (para o `signtool.exe`) e o cliente
   (`nuget install Microsoft.Trusted.Signing.Client`), que traz o `Azure.CodeSigning.Dlib.dll`.
3. Criar um `metadata.json`:
   ```json
   { "Endpoint": "https://<região>.codesigning.azure.net", "CodeSigningAccountName": "<conta>",
     "CertificateProfileName": "<perfil>" }
   ```
4. `az login` e depois:
   ```powershell
   powershell -File tools\publish.ps1 -SignMetadata metadata.json -SignDlib <caminho>\Azure.CodeSigning.Dlib.dll
   ```
   Assina o `LCapture.exe`/`.dll`, as DLLs do NAudio e o instalador.

## Se a imagem não aparecer

O Windows bloqueia por padrão o acesso de aplicativos de área de trabalho (Win32) à câmera. Se o
app mostrar "Acesso negado", vá em:

**Configurações do Windows → Privacidade e segurança → Câmera** → habilite
**"Permitir que aplicativos de área de trabalho acessem sua câmera"**.

## Notas de arquitetura

- `Services/VideoCaptureService.cs`: abre o dispositivo em modo exclusivo, lista os formatos nativos
  (`MediaFrameSource.SupportedFormats`) e transmite os frames via `MediaFrameReader`.
- `MainWindow.xaml.cs`: converte cada frame (`SoftwareBitmap` Bgra8) para um `WriteableBitmap` da
  UI copiando o buffer de pixels diretamente (sem reamostrar), descartando frames antigos se a UI
  não conseguir acompanhar a taxa da placa — a tela sempre mostra o quadro mais recente disponível.
- `MainWindow.Layers.cs`: as camadas. Cada fonte (placa, `WindowCaptureSource`, webcam) envia os
  seus quadros para `OnSourceFrame`, que os encaminha pelo papel atual (principal, sobreposição
  ou nenhum) — trocar as camadas não reinicia nada.
- `Services/FrameCompositor.cs`: cola a sobreposição no quadro principal (no processador; uma
  sobreposição de ~500 px num quadro 1080p custa pouco) e, durante a gravação, mantém todos os
  quadros no tamanho da gravação (letterbox se a fonte principal mudar de tamanho).
- `Services/RecordingService.cs`: `MediaStreamSource` (Bgra8 + áudio float) → `MediaTranscoder` →
  MP4. `Services/AudioMixer.cs` mistura as fontes de som num relógio próprio (QPC, 100 ms atrás do
  tempo real), por isso a faixa nunca tem buracos.
- `Services/ProcessAudioCapture.cs`: som de uma aplicação (WASAPI process loopback).
- Nada é enviado para a rede; as gravações ficam só no PC.

## Licença

Copyright (c) 2026 Leo Costa Developer. Todos os direitos reservados — o código é público apenas
para consulta (ver [LICENSE](LICENSE)). O LCapture inclui componentes de terceiros sob licença MIT
(NAudio, runtime .NET), listados em [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
