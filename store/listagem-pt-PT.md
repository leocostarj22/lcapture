# Textos da página na Microsoft Store (pt-PT)

Para colar no Partner Center > Envio > Listagens da Store > Português (Portugal).

## Descrição

Veja a sua consola, câmera ou outra fonte HDMI diretamente no ecrã do PC, com a menor latência
possível, e grave quando quiser. O LCapture mostra em tempo real a imagem e o som de uma placa de
captura de vídeo USB, sem recomprimir: o que chega da placa vai direto para o ecrã.

Carregue em Gravar (ou F9) para guardar um vídeo MP4 no seu PC, com a resolução, a taxa de quadros
e a qualidade que escolher. A codificação é feita pela placa gráfica, por isso o jogo e a imagem não
ficam mais lentos.

Monte a sua cena: webcam, logótipo, moldura ou uma janela como o browser, arrastados e
redimensionados diretamente na imagem, e troque a imagem principal com um clique. A gravação
mostra exatamente o que vê.

As gravações ficam só no seu PC. O LCapture não tem contas, publicidade nem ligação à internet.

## Novidades desta versão (1.2)

- Gravação para MP4 (H.264 + AAC) com escolha de resolução, taxa de quadros e qualidade
- Camadas livres: webcams, imagens (logótipo, moldura, marca de água) e janelas, arrastadas e
  redimensionadas diretamente na imagem
- Molduras que acompanham a webcam
- Vídeos animados (WebM, MP4) como camada, em ciclo
- Microfone na gravação, com volume próprio e medidor de nível
- Barra redesenhada e painel lateral de áudio
- Janela ou ecrã inteiro como imagem principal ou no canto; troca com um clique
- No Windows 11, grava também o som da janela capturada

## Funcionalidades (uma por linha)

- Imagem em tempo real da placa de captura, na qualidade máxima que ela suporta
- Gravação para MP4 com codificação pela placa gráfica (F9 para gravar e parar)
- Escolha da resolução, da taxa de quadros e da qualidade, com o tamanho estimado por hora
- Webcam, imagens, janela ou ecrã por cima da imagem, onde e do tamanho que quiser
- Logótipos e molduras com transparência (PNG), opacidade e ordem das camadas
- Troca da imagem principal com a sobreposição com um clique
- Som da placa reproduzido no PC, com controlo de volume e atraso reduzido
- Microfone na gravação, com volume e medidor de nível no painel Áudio
- Ecrã inteiro com F11 ou duplo clique; a barra e o cursor escondem-se sozinhos
- Filtro de nitidez opcional e reconexão automática da placa

## Descrição curta

Visualizador e gravador para placas de captura HDMI/USB: veja e grave a sua consola no PC.

## Palavras-chave (até 7)

placa de captura, HDMI, consola, gravar vídeo, webcam, baixa latência, capture card

## Notas de privacidade / URL

URL da política de privacidade: https://leocostarj22.github.io/lcapture/store/privacidade.html

## Requisitos (campo "Requisitos de sistema")

Placa de captura de vídeo USB compatível com UVC (aparece no Windows como câmera).

## Notas para a certificação (Opções de envio)

O LCapture mostra em tempo real o vídeo e o áudio de uma placa de captura USB (UVC) e, quando o
utilizador carrega em Gravar (ou F9), grava-os para um ficheiro MP4 na pasta Vídeos\LCapture do
utilizador. Não é necessária uma placa de captura para testar: o app funciona com qualquer webcam,
que escolhe automaticamente se não houver outra fonte. O botão Camadas permite pôr uma webcam ou uma
janela no canto da imagem; a captura de janelas usa Windows.Graphics.Capture, apenas com as janelas
que o utilizador escolhe na lista. Não tem contas, compras nem ligação à internet.

## Capacidades novas na 1.2 (se o Partner Center perguntar)

- `graphicsCaptureProgrammatic`: capturar a janela que o utilizador escolhe na lista do próprio
  LCapture (em vez do seletor do Windows), para saber de que aplicação gravar o som.
- `graphicsCaptureWithoutBorder`: no Windows 11, não desenhar a borda amarela à volta da janela
  capturada (aparece na gravação).

## Justificação do recurso restrito runFullTrust

O LCapture é uma aplicação de ambiente de trabalho WPF (.NET 10) empacotada em MSIX. O recurso
runFullTrust é necessário para que o executável Win32 corra como aplicação de ambiente de trabalho.
