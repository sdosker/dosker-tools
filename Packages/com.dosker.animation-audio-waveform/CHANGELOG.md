# Changelog

Todas as mudanças relevantes deste pacote ficam neste arquivo. O formato segue o
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

## [0.8.0] - 2026-11-09

### Adicionado

- Interface em inglês. O idioma é escolhido uma vez para todos os pacotes do Dosker Tools, em **Tools > Dosker Tools > Language**, em **Preferences > Dosker Tools** ou no botão com o globo. Automático segue o idioma do sistema: fora de português, a interface já abre em inglês.

### Alterado

- Passa a depender de `com.dosker.editor-ui`, que guarda a escolha de idioma.
- O menu **Tools > Dosker Tools > Animation Audio Waveform** mostra uma marca quando o painel está ligado e avisa na janela Animation ao ligar ou desligar. Ao ligar sem a janela Animation aberta, ela é aberta.

### Removido

- O menu de validação. Ele era só para desenvolvimento e agora fica fora do pacote.

## [0.7.0] - 2026-11-01

### Adicionado

- Solo por faixa e trava de faixas manuais.
- Compensação de latência.
- **Som ao arrastar**, opcional e desligado por padrão.
- Curva de volume do AudioSource no próprio clip.
- A janela separada explica por que o painel embutido não está ativo.

### Corrigido

- A waveform é refeita quando o áudio é reimportado.

## [0.6.2] - 2026-10-31

### Removido

- Aviso sobre IA neste pacote. Ele fica apenas no README principal e na página do listing.

## [0.6.1] - 2026-10-31

### Adicionado

- Aviso de que o projeto foi criado com inteligência artificial.

## [0.6.0] - 2026-10-31

### Alterado

- Primeira versão como pacote VPM, separada do ShaderView.
- Menus movidos para `Tools/Dosker Tools`.
