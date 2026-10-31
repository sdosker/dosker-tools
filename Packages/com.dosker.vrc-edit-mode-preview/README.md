# VRChat Edit-Mode Preview

Abra **Tools > Dosker Tools > VRChat Edit-Mode Preview** para visualizar fora do
Play mode componentes do VRChat SDK que normalmente só funcionam em execução.
O script acessa o SDK por reflexão: compila sem o SDK, mas só exibe algo com
o SDK de avatars ou worlds instalado pelo VCC.

A janela é organizada em cartões recolhíveis por categoria, com contador e um
olho que liga ou desliga o desenho na Scene View; a barra do topo concentra a
simulação. Os desenhos seguem o botão **Gizmos** da Scene View, exceto os pontos
de arraste dos PhysBones, que continuam visíveis e arrastáveis.

- **PhysBones:** cadeias, raio de colisão por osso (com curvas) e limites
  Angle/Hinge/Polar, para todos os componentes da cena (ou só da hierarquia
  selecionada), não apenas do objeto selecionado.
- **Simular PhysBones (temporário):** roda uma aproximação do solver (pull,
  spring, stiffness, gravity/falloff, immobile, colliders, limites). Mova o
  avatar ou arraste os pontos na Scene View (respeita **Allow Grabbing**). Ao
  arrastar, os ossos anteriores acompanham até a raiz, que fica fixa; limites de
  ângulo e colliders continuam valendo, mas stiffness é ignorado nesse trecho. A
  pose original é restaurada ao desligar, salvar a cena, recompilar ou entrar
  no Play mode. Enquanto simula, não edite a rotação dos ossos da cadeia.
- **Bloqueio:** com a simulação ligada, um objeto movido, girado ou escalado com
  as ferramentas da Scene View volta ao estado original ao soltar o mouse, sem
  deixar alteração na cena nem entrada de Undo.
- **PhysBone Colliders:** esfera, cápsula e plano; vermelho = *Inside Bounds*.
- **Contacts:** senders (laranja) e receivers (azul; verde quando colidindo).
  A janela calcula colisões por tags, *Allow Self/Others* (avatar definido pelo
  `VRCAvatarDescriptor`) e mostra o valor de cada receiver: Constant, OnEnter
  (pulso de 0,25 s) e Proximity (0 na borda, 1 no centro; *Use Face
  Proximity* em caixas mede a partir da face +Z). Com **Advanced Parameters**
  ligado no receiver, a janela mostra `_SenderType`, `_SenderLocalPosition*`,
  `_SenderLossyScale*`, `_SenderVelocity*`, `_PointSeparation`,
  `_ProximitySpeed` e `_CollisionTag_*` do sender mais próximo.
- **Material Contacts (SDK beta):** receivers com **Write to Shaders** recebem,
  no Edit mode, os mesmos buffers do `VRCContacts.cginc` nos
  MaterialPropertyBlocks dos renderers alvo (até 4):
  `_VRChatContact_Transforms_{nome}[9]` (até 8 senders em ordem de
  proximidade + a raiz do receiver no índice 8) e `_VRChatContact_Data_{nome}[9]`
  (player ID, bits das tags e, no índice 8, a quantidade de alvos). `{nome}` é o
  *Shader Parameter Override* ou, se vazio, o *Parameter*. No Edit mode, cada
  avatar (`VRCAvatarDescriptor`) recebe um player ID fictício a partir de 1. Os
  property blocks são limpos ao fechar a janela, desligar a opção ou entrar no
  Play mode.
- **VRC Constraints:** linhas até as fontes com o peso e um “fantasma” da pose
  resolvida (Parent, Position, Rotation, Aim, LookAt), sem mover o objeto.

É uma visualização aproximada: o solver real do VRChat (jobs nativos, stretch/
squish, permissões de rede, parâmetros de animator) só roda no Play mode/cliente.

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator
Companion e instale este pacote no projeto.
