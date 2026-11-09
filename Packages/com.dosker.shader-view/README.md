# ShaderView

Depurador de shaders para VRChat. Trabalha com **Unity 2022.3 (Built-in Render Pipeline)**, a versão
aceita pelo VRChat. A depuração, os testes, a renderização e o `GrabPass` acontecem diretamente no
Unity.

O shader `ScreenSpace Ubershader_D` não vem neste pacote. O debugger o localiza pelo GUID, em qualquer
pasta do projeto.

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator
Companion e instale este pacote no projeto.

## Abrir

1. Execute **Tools > Dosker Tools > ShaderView > Create exact Unity preview** para criar a cena de
   teste em `Assets/ShaderView`.
2. Abra **Tools > Dosker Tools > ShaderView > VR Camera Debugger**.

## Visualizações e contexto do shader

- A prévia VR mostra sempre os dois olhos, lado a lado. As câmeras esquerda e
  direita seguem somente a posição e a rotação da Scene View ativa. FOV,
  proporção, IPD, convergência e resolução vêm das configurações do debugger.
  **Capturar todas** atualiza ambos os olhos; com atualização contínua
  acompanham a Scene View no FPS escolhido.
- As demais imagens são câmeras mono nas distâncias configuradas, medidas a
  partir do objeto do shader. A câmera de referência captura o mundo sem o
  objeto-alvo e depois desenha o alvo preservando a cor, mas limpando o depth;
  isso impede que a geometria ao redor forneça profundidade ao shader. As outras
  câmeras renderizam o mundo completo com depth texture e DepthLight.
- Os cartões mostram apenas a distância; a primeira estação também indica
  **sem depth**. As configurações da DepthLight são descritas uma vez, sem
  repetir intensidade e sombras em cada cartão.
- **Primeira distância** e **Intervalo entre câmeras** aceitam valores a partir
  de **0,1 m**.
- Use **Rotação em volta do alvo** para girar todas as câmeras mono ao redor
  do objeto em até **360°**; os marcadores na Scene View acompanham essa rotação.
- O mesmo contexto simulado é aplicado a todas as câmeras de distância:
  VR/desktop, olho esquerdo/direito, espelho, espelho pessoal, modo de câmera
  (regular, câmera VR/desktop de mão ou foto), visibilidade da UI de câmera e
  altura de tela.
- As câmeras usam `Camera.Render`, o shader original, os `GrabPass` e a depth
  texture do Built-in Render Pipeline.
- As demais DepthLights são Directional, pretas, intensidade **0,01**, sombras
  **Hard** e bake **Baked**. Ficam ativadas somente durante a captura.
- **Atualização contínua** inicia ativada em **Nativo (Scene View)**: uma nova
  captura é solicitada a cada redesenho da Scene View, seguindo seu ritmo nativo
  em vez de limitar a 1 FPS. Também é possível escolher 2, 5, 10, 15, 30 ou 60
  capturas por segundo. O seletor aparece em uma linha separada para manter o
  valor visível; as capturas não escrevem uma mensagem no Console a cada frame.
- A aba **Estéreo** renderiza os dois olhos em uma única imagem lado a lado.
  FOV, proporção de cada olho, IPD, convergência, inclinação e assimetria da
  projeção podem ser ajustados para visualizar desalinhamentos estéreo. Essa
  aba captura somente a imagem estéreo Pimax; ela não dispara as câmeras mono
  de distância.
- Arraste o divisor vertical para redimensionar o painel de configurações.
  A janela Unity também pode ser redimensionada; use **Altura das prévias** e
  **Resolução (altura simulada)** para ajustar tamanho exibido e qualidade das
  imagens. A resolução é a altura real da textura, que o shader lê para
  classificar a câmera; quanto maior, mais pesada a atualização contínua.
  Configure **Colunas mono por linha** para exibir mais câmeras de distância
  lado a lado (de 1 a 8).
  As configurações e a lista de capturas têm rolagem independente.
- Nas duas abas, a área da direita é reservada às câmeras: avisos, contadores
  e explicações ficam no painel esquerdo (seção recolhível **Sobre as
  capturas**). A prévia VR e as câmeras mono rolam juntas, então basta rolar
  até as distâncias de interesse sem redimensionar a janela.
- O inspector customizado do shader não é embutido na janela, pois ocupava
  espaço das imagens. Selecionar um material **não é necessário para capturar**:
  as câmeras renderizam o material que já está nos renderers do objeto. O campo
  opcional **Material do Inspector** serve apenas para abrir esse material no
  Inspector normal e editar suas propriedades; se estiver vazio ou inválido, o
  debugger tenta usar o primeiro material dos renderers do objeto selecionado.
  Uma seleção manual válida é preservada. Com atualização contínua, as capturas
  refletem as alterações na frequência escolhida; sem ela, clique em
  **Capturar todas**.

## Perfis, globais e custo

- As configurações ficam em `UserSettings/ShaderView.json`. O cartão **Perfis e
  globais** salva conjuntos com nome, para alternar entre testes.
- **Luz ambiente** troca a luz ambiente só durante a captura.
- **Globais extras** define floats globais só durante a captura, para shaders
  que leem algo como AudioLink ou Light Volumes.
- O cartão **Alvo** lista as globais do VRChat citadas no arquivo do shader.
  Includes não entram nessa lista.
- **Usar a seleção como alvo** aceita qualquer objeto. Só a cena de teste
  depende do `ScreenSpace Ubershader_D`.
- A barra do topo mostra o tempo e a memória da última captura.
- A aba **Estéreo** tem faixas de FOV aproximadas, que não representam um modelo
  específico de headset.

## Limites da simulação

O debugger define as mesmas globais que o cliente do VRChat — `_VRChatMirrorMode`,
`_VRChatMirrorCameraPos`, `_VRChatFaceMirrorMode`, `_VRChatCameraMode` e
`_VRChatCameraMask` — apenas enquanto cada imagem é capturada, restaurando-as em
seguida. Para simular a visão principal em VR, define também
`_ShaderView_SimulatedVR`, `_ShaderView_SimulatedEyeIndex` e
`_ShaderView_SimulatedCameraCenter`, que o VRChat nunca define. Para os filtros
por contexto terem efeito, o material precisa de **Overlay/Visibility** ligado;
a janela avisa quando está desligado ou quando o shader é anterior à simulação.

- **Funciona:** espelho VR e desktop, olho no espelho VR, espelho pessoal,
  câmeras de mão, foto, classificações por resolução e visão principal em VR,
  com filtros por olho.
- **Simulação de VR:** ligada na prévia VR, na aba Estéreo e, com **É VR**, nas
  câmeras mono, sempre fora de espelho, espelho pessoal, câmera de mão e foto.
  As câmeras comuns continuam com uma textura por olho; os ramos de aparência
  (UV do overlay, Boss Bar e aberração radial) seguem o caminho de Single Pass
  Stereo, o do cliente de PC em junho de 2026. Se o VRChat adotar SPS-I, esses
  três ramos passam a divergir do cliente.
- **Posições na tela:** seguem o FOV da câmera de captura. A Boss Bar usa
  unidades de tangente e muda com o FOV.
- **Câmera VR de mão:** segue o filtro `Camera_Desktop`, porque `Camera_VR` fica
  dentro de `isVR()`, falso em câmera não stereo.
- **Espelho pessoal em VR:** não se sabe se esse render é stereo, então o filtro
  de espelho pessoal em VR só pode ser conferido no VRChat.
- **Espelho:** muda o que o shader decide, mas a imagem não reflete a geometria.

Isso simula as ramificações do shader, mas não cria um runtime XR/VRChat: tempo
sincronizado, foveação e distorção óptica exigem o runtime e, para validação
final, o headset correspondente.

### Estéreo: escopo da simulação

A projeção VR é específica por olho: o OpenVR fornece projeções brutas separadas
e uma transformação de cada olho para a cabeça. A aba **Estéreo** reproduz
visualmente essa estrutura com duas câmeras, frusta assimétricas, separação dos
olhos e inclinação ajustável, compondo os resultados em uma textura única.
Consulte a documentação da Valve para
[GetProjectionRaw](https://github.com/ValveSoftware/openvr/wiki/IVRSystem%3A%3AGetProjectionRaw)
e
[GetEyeToHeadTransform](https://github.com/ValveSoftware/openvr/wiki/IVRSystem%3A%3AGetEyeToHeadTransform),
e a documentação Unity sobre
[renderização estéreo](https://docs.unity3d.com/2022.3/Documentation/Manual/SinglePassStereoRendering.html).

Esta aba é uma ferramenta de diagnóstico visual, não uma reprodução calibrada de
um modelo específico de Pimax: fora de um runtime XR, o Editor não tem as
matrizes ópticas reais fornecidas pelo headset/SteamVR. Ela também não aplica
distorção de lente nem corrige automaticamente o shader. Use os parâmetros para
expor a sensibilidade do shader à diferença entre os olhos; a confirmação final
do problema exige o modelo e runtime reais.

A referência não modifica o shader: o isolamento é feito apenas no script de
captura, movendo temporariamente o objeto-alvo para uma layer livre. A câmera
de fundo exclui essa layer; a segunda câmera inclui somente ela e limpa o depth
antes de desenhar o alvo. O script restaura as layers originais após capturar.
As demais câmeras continuam vendo o mundo completo. A DepthLight é uma luz
separada e não produz a depth texture. O modo **Baked** configura a luz como
solicitado, mas não executa um novo bake de lightmaps.
