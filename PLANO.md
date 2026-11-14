# Plano de melhorias — LCapture

## Fase 1 — Robustez para outros utilizadores
- [ ] Migrar para .NET 10 (o .NET 8 perdeu suporte em 10/11/2026)
- [ ] Vídeo: escolha automática evita webcams integradas (prefere dispositivos externos)
- [ ] Áudio: escolher o áudio da mesma placa de captura (ContainerId); nunca cair no microfone por acaso
- [ ] Ícone de mudo correto ao abrir
- [ ] Gravação das definições do volume com debounce
- [ ] Atualizar (F5 / botão) não derruba a captura em curso; F5 também recarrega o áudio
- [ ] README: caminho correto do executável

## Fase 2 — Distribuição
- [ ] Script `tools/publish.ps1`: build self-contained (não precisa de .NET instalado)
- [ ] Instalador Inno Setup (`installer/LCapture.iss`): atalhos, desinstalador, aviso de privacidade da câmera
- [ ] Assinatura de código (Azure Trusted Signing) — decisão/conta do autor

## Fase 3 — Desempenho e experiência
- [ ] Pedir frames já em BGRA ao Media Foundation (menos cópias/conversão na CPU)
- [ ] Áudio: limitar a latência acumulada (descartar excesso acima de ~80 ms)
- [ ] Barra de ferramentas esconde-se sozinha após inatividade
- [ ] Reconexão automática quando a placa é desligada/ligada (DeviceWatcher)
- [ ] Desligar nitidez quando a renderização for por software (RDP/VM)
- [ ] Resolução padrão pelo monitor onde está a janela
