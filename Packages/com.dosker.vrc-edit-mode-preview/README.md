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
  spring, gravity/falloff, immobile, colliders, limites, stretch e squish;
  stiffness só em **Advanced**, e **Immobile World** desconta apenas o
  deslocamento do avatar). Mova o
  avatar ou arraste os pontos na Scene View (respeita **Allow Grabbing**). Ao
  arrastar, os ossos anteriores acompanham até a raiz, que fica fixa; limites de
  ângulo e colliders continuam valendo, mas stiffness é ignorado nesse trecho. A
  pose original é restaurada ao desligar, salvar a cena, recompilar ou entrar
  no Play mode. Enquanto simula, não edite a rotação dos ossos da cadeia.
- **Armature Link do VRCFury:** com **Seguir Armature Link** ligado, os ossos
  ligados da roupa acompanham os do avatar durante a simulação, como depois do
  build. Assim, um PhysBone do avatar também move a roupa. A correspondência
  segue o nome dos ossos; os casos especiais do VRCFury para `ChestUp`, a escala
  de alinhamento e Armature Links criados por outros prefabs só no build não
  entram. A cena não muda: tudo volta ao parar.
- **Animações de teste:** geradas sobre os músculos humanoides e sem arquivos de
  animação, para testar clipping e PhysBones em movimento:
  - **Movimento:** Parado, Andar, Correr, Agachado, Andar agachado, Sentado e Pular.
  - **Poses:** T-pose e A-pose como um alongamento: o avatar fica em pé, ajeita os
    ombros, entra na pose, fica um tempo abrindo o peito e girando o tronco, e volta.
  - **Pernas:** Levantar joelhos, Calcanhar para trás, Levantar perna esticada,
    Agachamento e Abrir as pernas.
  - **Tronco e braços:** Erguer os braços, Abraçar o corpo, Torcer o tronco,
    Inclinar para os lados e Olhar em volta.

  O avatar anda no lugar, e a pose original volta ao escolher **Nenhuma** ou parar
  a simulação. Escolher uma animação liga a simulação. Um movimento lento por cima
  evita que o ciclo se repita igual, então não há um ponto de recomeço visível.
- **Posar e sacudir:** com **Posar** ligado, o osso solto mantém a pose quando
  **Allow Posing** permite; **Reiniciar** desfaz. O botão de sacudir dá o mesmo
  impulso a todas as cadeias, para comparar ajustes.
- **Parâmetros:** cada cadeia mostra `_Angle`, `_Stretch`, `_Squish`,
  `_IsGrabbed` e `_IsPosed`. O cartão também mostra uma contagem aproximada de
  transforms, colliders e collision checks.
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

- **Busca:** filtra PhysBones e receivers pelo nome.
- **Tags de contacts:** mostra quantos senders e receivers usam cada tag e avisa
  quando uma tag não tem par na cena.
- **Verificar configuração:** aponta PhysBones sem efeito, ossos controlados por
  dois PhysBones, referências vazias, colliders sem uso, constraints sem fontes
  ou sem peso e ciclos entre constraints.
- **Atualizações do SDK:** a janela avisa quando um campo usado não existe mais.
- **Preferências:** ficam em `UserSettings/VrcEditModePreview.json`.

**Tools > Dosker Tools > Validar Edit-Mode Preview** confere os cálculos e os
campos do SDK e registra `VRC_PREVIEW_VALIDATION_OK` no Console. O mesmo teste
roda com `-executeMethod VrcEditModePreviewWindow.RunAutomatedValidation`.

É uma visualização aproximada: o solver real do VRChat (jobs nativos, permissões
de rede, parâmetros de animator) só roda no Play mode/cliente.

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator
Companion e instale este pacote no projeto.
