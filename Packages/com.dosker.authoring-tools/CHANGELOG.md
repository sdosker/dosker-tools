# Changelog

Todas as mudanças relevantes deste pacote ficam neste arquivo. O formato segue o
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

## [0.9.0] - 2026-11-21

### Adicionado

- **Prévia do menu de expressões** no Inspector de um `VRCExpressionsMenu`, abaixo dos campos do SDK, com o visual do Action Menu do VRChat: Back no topo, fatias no sentido horário, ícones, marcadores de tipo, toggles e botões ligados girando e a porcentagem dos radial puppets.
- Os nomes seguem o rich text do TextMeshPro, que o VRChat usa: cor, tamanho, negrito, itálico, sublinhado, riscado, marca-texto, sobrescrito, subscrito, maiúsculas, quebras de linha e as demais tags do TMP. Uma tag que o TMP não aceita, como `<Color=red>`, aparece como texto, igual ao jogo.
- Barra de formatação embaixo do campo **Name** (e dos rótulos dos puppets) do editor do SDK: negrito, itálico, sublinhado, riscado, cor, tamanho e **Limpar**, aplicados ao trecho selecionado ou ao nome todo, sem precisar decorar as tags.
- A prévia acompanha a edição em tempo real: o que é digitado no Inspector, um Undo ou uma mudança feita em outra janela aparecem na hora.
- A prévia é interativa: toggles, botões, submenus e puppets mudam os parâmetros como no VRChat, com os valores iniciais do avatar da cena que usa o menu. O botão direito volta.
- Janela **Tools > Dosker Tools > Expressions Menu Preview**, que segue a seleção (menu ou avatar) e mostra os parâmetros para editar.

### Alterado

- O pacote passa a se chamar **Authoring Tools** (`com.dosker.authoring-tools`) e vai reunir as ferramentas que ajudam a editar conteúdo do VRChat no Unity. O Animation Audio Waveform é a primeira delas e funciona como antes: o menu e as preferências continuam os mesmos.
- Substitui `com.dosker.animation-audio-waveform`. Ao instalar este pacote, o VCC remove o antigo.

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
