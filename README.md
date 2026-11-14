# LCapture

Visualizador de vídeo para Windows, feito para exibir — apenas para visualização, sem gravação —
o sinal de uma fonte de captura de vídeo USB (UVC), como uma placa de captura Fifine AmpliGame V3
ligada via HDMI. Usa a API nativa `Windows.Media.Capture` para obter os frames diretamente da placa
com a maior qualidade/resolução nativa que ela anunciar, sem recompressão.

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

O executável fica em `src/LCapture/bin/x64/Release/net10.0-windows10.0.19041.0/LCapture.exe`.

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

## Se o app fechar ao abrir ("Uma política de Controle de Aplicativos bloqueou este arquivo")

É o **Smart App Control** do Windows 11, que bloqueia binários sem assinatura digital (como o que
o `dotnet build` gera). Nas primeiras semanas ele fica em modo "avaliação" (não bloqueia) e depois
passa a bloquear. Soluções: assinar o `LCapture.dll`/`.exe` com um certificado de assinatura de
código publicamente confiável (certificado autoassinado NÃO satisfaz o Smart App Control), ou
desativar o Smart App Control em Segurança do Windows > Controle de aplicativos e navegador
(atenção: depois de desligado, só volta com reinstalação do Windows).

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
- Nada é gravado, encodado ou enviado para disco/rede: é um visualizador puro.
