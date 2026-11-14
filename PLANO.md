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
- [ ] Assinatura de código (Azure Trusted Signing) — decisão/conta do autor

## Fase 3 — Desempenho e experiência
- [ ] Menos cópias/conversão de frames na CPU — pedir Bgra8 ao MediaFrameReader deu ecrã
      preto na Fifine; próxima tentativa: renderização Direct3D 11 (D3DImage) ou converter
      YUY2 num shader
- [x] Áudio: limitar a latência acumulada (descarta excesso acima de 100 ms; saída WASAPI 40 ms)
- [x] Barra de ferramentas em linha própria (não tapa o vídeo); em ecrã inteiro/recolhida aparece ao passar o rato no topo
- [x] Reconexão automática quando a placa é desligada/ligada (DeviceWatcher)
- [x] Desligar nitidez quando a renderização for por software (RDP/VM)
- [x] Resolução padrão pelo monitor onde está a janela
