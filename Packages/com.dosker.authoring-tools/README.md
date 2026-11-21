# Authoring Tools

Ferramentas que ajudam a editar conteúdo do VRChat no Unity. Este pacote substitui o
`com.dosker.animation-audio-waveform`: ao instalá-lo, o VCC remove o antigo, e as preferências continuam as mesmas.

## Expressions Menu Preview

Mostra o menu radial do VRChat no Inspector de um `VRCExpressionsMenu`, abaixo dos campos do SDK. O editor do
SDK continua o mesmo: a prévia só é acrescentada no fim, numa seção **Prévia no VRChat** que pode ser recolhida.

- **Visual do Action Menu:** Back no topo e os controles no sentido horário, ícone com o nome embaixo e o
  marcador do tipo (toggle, submenu, radial, dois e quatro eixos). Toggles e botões ligados mostram o anel
  girando, e os radial puppets mostram a porcentagem. Só os 8 primeiros controles aparecem, como no jogo.
- **Texto como no VRChat:** os nomes passam pelas regras de rich text do TextMeshPro, que o VRChat usa.
  Funcionam `<color>` (nomes e `#RGB`, `#RGBA`, `#RRGGBB`, `#RRGGBBAA`), `<#FF0000>`, `<alpha>`, `<size>`
  (absoluto, `+`/`-`, `%` e `em`), `<b>`, `<i>`, `<u>`, `<s>`, `<mark>`, `<sup>`, `<sub>`, `<br>`, `
`,
  `<uppercase>`, `<lowercase>`, `<smallcaps>`, `<cspace>`, `<mspace>`, `<voffset>`, `<line-height>`,
  `<align>`, `<nobr>`, `<noparse>`, `<scale>` e `<rotate>`. Como no TMP, o nome da tag só vale todo em
  minúsculas ou todo em maiúsculas, e os nomes de cor só em minúsculas: `<Color=red>` e `<color=Red>`
  aparecem como texto. O texto quebra nos espaços, com tamanho base 24 numa caixa de 200 unidades, num menu
  de 600 de diâmetro.
- **Formatação sem decorar tags:** embaixo do campo **Name** do controle, e dos rótulos dos puppets, há uma
  barra com **B**, **I**, **U**, **S**, uma cor com **Colorir**, um tamanho com **Tamanho** e **Limpar**.
  Selecione um trecho do nome e clique; sem seleção, vale para o nome todo. **Limpar** tira as tags que o
  VRChat reconhece e mantém o resto.
- **Tempo real:** a prévia muda enquanto o nome é digitado, e também com Undo ou com mudanças feitas em
  outra janela, sem precisar clicar em nada.
- **Interativo:** clique para ligar toggles, apertar botões (voltam a zero depois de cerca de um segundo),
  entrar em submenus e abrir puppets. Arraste para mexer no puppet; o botão direito volta. Um submenu que
  define um parâmetro o zera ao sair, e mudar esse parâmetro fecha o submenu, como diz a documentação do
  VRChat. Os valores iniciais vêm do `VRCExpressionParameters` do avatar da cena que usa o menu.
- **Janela:** **Tools > Dosker Tools > Expressions Menu Preview**, ou o botão **Janela** da prévia, abre o
  menu numa janela maior que segue a seleção, seja um menu ou um avatar, com os parâmetros editáveis ao lado.
- **Limites:** a fonte é a Arial, que tem as mesmas medidas da LiberationSans, a fonte padrão do TMP. O
  formato das letras pode variar um pouco em relação ao jogo, mas as quebras de linha e os tamanhos não.
  `<sprite>` vira um marcador, e `<font>`, `<material>`, `<gradient>` e `<style>` aparecem como texto,
  porque dependem de assets do cliente. As cores e medidas do menu seguem o Action Menu padrão; o
  tamanho e a opacidade que cada jogador escolhe nas opções do VRChat não entram.
- **Requisitos:** só aparece em projetos com `com.vrchat.avatars`. Em projetos de mundo, o resto do pacote
  continua funcionando.

## Animation Audio Waveform

Mostra o áudio dentro da janela **Animation**, abaixo da dopesheet/curvas, para
sincronizar animações com som. **Tools > Dosker Tools > Animation Audio Waveform**
liga ou desliga o painel.

- **Faixas automáticas:** os AudioSources com clip ligados pelo clip atual
  aparecem sozinhos, seja por toggle do próprio GameObject, de um objeto pai ou
  do componente `AudioSource`. A waveform começa no ponto exato de ativação,
  indicado na faixa (ex.: `0:30 · frame 30`), e termina no fim do áudio ou no
  desligamento. Só fontes com **Play On Awake** iniciam com **Tocar** ligado.
- **Faixas manuais:** arraste um `AudioClip` em **Adicionar áudio**. Defina o
  início pelo campo numérico ou arrastando a waveform.
- **Várias faixas:** cada uma tem **Ver**, **Tocar** e cor próprios. Zoom e pan
  acompanham a timeline. Clicar ou arrastar fora de uma faixa manual move a
  playhead. Arraste a borda superior para redimensionar o painel.
- **Reprodução:** o áudio só toca com a animação rodando continuamente. Pausar,
  arrastar a playhead e andar frame a frame ficam em silêncio. A posição do som
  sempre deriva do tempo da animação menos o ponto de ativação; por isso loops,
  seeks e retomadas continuam sincronizados. Com **Tocar com a animação**
  desligado, nada toca. Volume, pitch, loop e mixer vêm do AudioSource. Uma curva
  de volume do AudioSource no próprio clip é seguida; curvas de pitch não são.
- **Escuta:** **S** deixa só uma faixa tocando, e **L** trava uma faixa manual
  contra arrastes. **Som ao arrastar**, desligado por padrão, toca um trecho curto
  ao arrastar a playhead neste painel. O campo **ms** adianta o som, para compensar
  o atraso do fone ou da saída de áudio.
- **Áudio 3D:** com **Áudio 3D (câmera)** ligado, cada faixa automática muda em
  tempo real conforme a câmera do Scene view (ou a `Camera.main`, sem Scene
  view) e o GameObject do AudioSource. A distância define o volume e a direção
  define o lado, seguindo Spatial Blend, Stereo Pan, Volume Rolloff, Min/Max
  Distance e as curvas customizadas da própria fonte. As curvas seguem o Unity:
  o rolloff logarítmico continua caindo depois de **Max Distance**, enquanto o
  linear chega a zero nele.
- **Limites do áudio 3D:** é pan estéreo, como no Unity sem spatializer: frente
  e trás soam iguais, e não há HRTF, Spread, Doppler nem reverb. Clips estéreo
  são aproximados, pois o Unity soma os canais em mono e aqui um canal não passa
  para o outro lado. Faixas manuais não têm objeto e ficam 2D. O
  `VRCSpatialAudioSource` do VRChat não é simulado, então o cliente pode soar
  diferente.
- **Não altera o projeto:** os AudioSources reais não são tocados. O som vem de
  fontes ocultas destruídas ao sair do preview, recompilar ou entrar no Play
  mode. As preferências ficam em `UserSettings/AnimationAudioWaveform.json`.

O painel embutido usa o **Harmony** encontrado no projeto, por reflexão. O
VRChat SDK já inclui essa biblioteca em `com.vrchat.base`; não adicione outra
cópia sem necessidade. Sem Harmony, o menu abre uma janela separada com o mesmo
conteúdo, que se alinha à timeline quando encaixada abaixo da janela Animation.

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator
Companion e instale este pacote no projeto.
