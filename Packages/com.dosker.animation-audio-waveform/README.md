# Animation Audio Waveform

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
