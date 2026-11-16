# Plano de melhorias — LCapture

## Fase 1 — Robustez para outros utilizadores
- [x] Migrar para .NET 10 (o .NET 8 perdeu suporte em 10/11/2026)
- [x] Vídeo: escolha automática evita webcams integradas (prefere dispositivos externos)
- [x] Áudio: escolher o áudio da mesma placa de captura (ContainerId); nunca cair no microfone por acaso
- [x] Ícone de mudo correto ao abrir
- [x] Gravação das definições do volume com debounce
- [x] Atualizar (F5 / botão) não derruba a captura em curso; F5 também recarrega o áudio
- [x] README: caminho correto do executável

## Fase 2 — Distribuição
- [x] Script `tools/publish.ps1`: build self-contained (não precisa de .NET instalado)
- [x] Instalador Inno Setup (`installer/LCapture.iss`): atalhos, desinstalador, aviso de privacidade da câmera
- [x] Instalador mais leve: só NAudio.Wasapi (sem WinForms) e traduções pt/en (61 → 53 MB)
- [x] `publish.ps1` pronto para assinar (Azure Trusted Signing: -SignMetadata/-SignDlib)
- [ ] Criar a conta Trusted Signing e assinar a primeira versão — depende do autor

## Fase 3 — Desempenho e experiência
- [x] Menos trabalho na CPU por frame: Media Foundation entrega Bgra8 (fallback para o
      formato nativo) e o frame vai para o ecrã sem cópia intermédia — 1,23 → 0,87 núcleos
      em 1080p60 (a 1.ª tentativa pareceu dar ecrã preto, mas era a consola sem imagem)
- [x] Áudio: limitar a latência acumulada (descarta excesso acima de 100 ms; saída WASAPI 40 ms)
- [x] Barra de ferramentas em linha própria (não tapa o vídeo); em ecrã inteiro/recolhida aparece ao passar o rato no topo
- [x] Reconexão automática quando a placa é desligada/ligada (DeviceWatcher)
- [x] Desligar nitidez quando a renderização for por software (RDP/VM)
- [x] Resolução padrão pelo monitor onde está a janela

## Fase 4 — Microsoft Store (MSIX)
- [x] Conta "Leo Costa Developer" criada e nome LCapture reservado (Store ID 9MTK97FH4JQ0)
- [x] Logótipos da Store (`tools/make-store-assets.ps1` → `packaging/Assets`)
- [x] Manifesto MSIX (`packaging/AppxManifest.template.xml`): runFullTrust, câmera, microfone
- [x] `tools/package-msix.ps1`: gera o .msix (e `-TestSign` para testar localmente)
- [ ] Instalar o pacote de teste e correr o Windows App Certification Kit (precisa de admin)
- [x] Identidade real do Partner Center no pacote
- [x] Política de privacidade no GitHub Pages, textos, capturas de ecrã e logótipos (`store/`)
- [x] Envio 1 (v1.1.1.0) submetido para certificação em 14/11/2026
- [x] Certificado e publicado na Microsoft Store
- [x] Link https://apps.microsoft.com/detail/9MTK97FH4JQ0 no README

## Fase 5 — Gravação com definição ajustável
Pipeline próprio, só com APIs do Windows (sem FFmpeg): os quadros Bgra8 (já compostos com a
sobreposição) e o áudio misturado vão para um `MediaStreamSource`, e um `MediaTranscoder` codifica
em H.264 + AAC para MP4 com o codificador de hardware da placa de vídeo.
- [x] `RecordingService`: MediaStreamSource (Bgra8 + áudio float 48 kHz) → MediaTranscoder → MP4
- [x] Mesmo relógio para imagem e som (QPC); `AudioMixer` com relógio próprio, sem buracos na faixa
- [x] Definições: resolução, fps (original ou 30), qualidade Alta/Média/Baixa com estimativa GB/h
- [x] Botão Gravar/Parar com contador; atalho F9; pasta (Vídeos\LCapture) e botão para a abrir
- [x] Fila limitada (descarta quadros se o codificador não acompanhar) e aviso no fim
- [x] Fecha bem o ficheiro ao trocar a qualidade/fonte principal, desligar a placa, fechar o app,
      ou com pouco disco; trocar o áudio ou as camadas já não interrompe a gravação
- [x] Aviso quando não há codificador H.264 de hardware (MFTEnumEx)
- [x] Teste sem placa (`tests/RecordingSmokeTest`): orientação, sobreposição, 0 quadros perdidos
      em 1080p60, som 440 Hz com RMS certo (inclui conversão 44,1 kHz mono → 48 kHz estéreo)
- [ ] Gravação à prova de falhas: o MediaTranscoder não aceita MP4 fragmentado (testado); exige
      reescrever para o SinkWriter do Media Foundation — fica para depois da 1.2

## Fase 6 — Camadas + webcam picture-in-picture
- [x] Camada principal (placa ou janela/ecrã) + sobreposição (webcam, janela/ecrã ou placa)
- [x] Encaminhamento por papel (`OnSourceFrame`): trocar camadas não reinicia as fontes
- [x] Webcam pedida em ≤ 720p, de preferência MJPG (largura de banda USB); erro explica a porta USB
- [x] `FrameCompositor`: sobreposição desenhada antes do ecrã e do gravador; 4 cantos, 3 tamanhos;
      durante a gravação mantém o tamanho do vídeo (letterbox) — ~8 ms/quadro no pior caso 1080p
- [x] Aviso 4K60 + sobreposição ao gravar

## Fase 7 — Janela/ecrã como fonte + áudio por aplicação
- [x] `WindowCaptureSource` (Windows.Graphics.Capture) com lista própria de janelas e ecrãs
- [x] Trocar principal ↔ sobreposição com um clique
- [x] Esconder a borda amarela no Windows 11 (`IsBorderRequired = false`, com pedido de acesso)
- [x] Som só da aplicação da janela (WASAPI process loopback; ecrã inteiro = tudo menos o LCapture),
      apenas durante a gravação. Requer Windows 11 (build 20348+), não Windows 10
- [x] Janela parada: o último quadro é reenviado nas trocas de camada e durante a gravação
- [x] Avisos: vídeo protegido preto, janela minimizada, janela fechada

## Microfone e barra nova
- [x] Microfone na gravação: fonte, mudo, volume (0–100%), medidor, "Gravar o microfone";
      nunca toca nas colunas; só ligado com o painel Áudio aberto ou a gravar
- [x] Barra redesenhada: uma linha (fontes | ações), painel lateral Áudio, barra de estado em baixo
- [x] Registo de diagnóstico (`%LOCALAPPDATA%\LCapture\lcapture.log`) e `tools/run-dev.ps1`
      (o Windows não deixa apps em `\\wsl.localhost` usar a câmera)

## Cena com camadas livres
- [x] Camadas sem limite: webcams, imagens (PNG com transparência), janela, fonte da barra
- [x] Arrastar e redimensionar na imagem (Shift = livre), encaixe nas margens/centro, Delete/Esc
- [x] Ordem, opacidade, mostrar/esconder (webcam: liga/desliga), posições rápidas, bloquear
- [x] Botão direito numa camada: Ajustar ao ecrã, Esticar, posição, ordem, esconder, remover
- [x] Camada de vídeo (WebM/MP4) em ciclo, sem som, via MediaPlayer + Direct3D; "Fundo a remover"
      (preto/branco) porque o Windows perde o alfa do WebM — testado com uma moldura VP9 real
- [x] "Acompanhar" + "Ajustar": moldura presa à webcam
- [x] Cena guardada (imagens copiadas para a pasta da app; webcams voltam desligadas)
- [x] Compositor: só desenha as partes visíveis das imagens — moldura 1080p + webcam ≈ 5 ms/quadro

## Lançamento da 1.2 (depois de testar no PC real)
- [x] Versão 1.2.0; política de privacidade, textos da Store e README atualizados
- [ ] Testar no PC com a placa (lista abaixo); corrigir o que aparecer
- [ ] Gerar o .msix e confirmar que o manifesto com as capacidades `uap11` passa no makeappx/WACK
- [ ] Capturas de ecrã novas para a Store (botão Gravar, webcam no canto)
- [ ] Push (publica a nova política no GitHub Pages) e novo envio no Partner Center

### Testes no PC real
1. Placa: Iniciar, Gravar 1 min com som, Parar; abrir o MP4 (imagem direita, som sincronizado).
2. Gravação longa (1 h) para ver se o som se afasta da imagem.
3. Webcam no canto: ligar, mudar canto/tamanho, gravar. Webcam na mesma porta USB que a placa.
4. Browser como principal e como sobreposição; Trocar; redimensionar a janela durante a gravação;
   minimizar; fechar a janela.
5. Som do browser na gravação (Windows 11) sem eco do som da placa; ecrã inteiro.
6. Mudar a fonte de áudio e o mudo durante a gravação.
7. Microfone: escolher, ver o medidor mexer, gravar placa + microfone; mudo do microfone a meio.
8. Camadas: imagem PNG, arrastar/redimensionar, moldura a acompanhar a webcam, gravar e confirmar
   que a moldura de seleção azul não aparece no vídeo.
