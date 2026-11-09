# VRChat Edit-Mode Preview

Abra **Tools > Dosker Tools > VRChat Edit-Mode Preview** para visualizar fora do
Play mode componentes do VRChat SDK que normalmente só funcionam em execução.
O script acessa o SDK por reflexão: compila sem o SDK, mas só exibe algo com
o SDK de avatars ou worlds instalado pelo VCC.

A janela é organizada em cartões recolhíveis por categoria, com contador e um
olho que liga ou desliga o desenho na Scene View; a barra do topo concentra a
simulação. Os desenhos seguem o botão **Gizmos** da Scene View, exceto os pontos
dos ossos que podem ser pegos, que continuam visíveis durante a simulação.

- **PhysBones:** cadeias, raio de colisão por osso (com curvas) e limites
  Angle/Hinge/Polar, para todos os componentes da cena (ou só da hierarquia
  selecionada), não apenas do objeto selecionado.
- **Simular PhysBones (temporário):** roda o `PhysBoneManager` do VRChat SDK no
  Edit mode, o mesmo solver do Play mode e do Gesture Manager. O SDK não prevê
  esse uso: se ele não puder iniciar, a janela avisa e usa um solver aproximado
  (status **Aproximada**). Com o solver do SDK, a simulação pausa quando o Unity
  perde o foco (status **pausada sem foco**), e ajustes feitos no Inspector valem
  na hora. Mova o avatar ou pegue um osso na Scene View (respeita **Allow
  Grabbing**). A pose original é restaurada ao desligar, salvar a cena, recompilar
  ou entrar no Play mode. Enquanto simula, não edite a rotação dos ossos da cadeia.
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
- **Posar e sacudir:** com o botão esquerdo pressionado, o direito posa o osso,
  como no Play mode; com **Posar** ligado, soltar o botão faz o mesmo. Vale quando
  **Allow Posing** permite, e **Reiniciar** desfaz. O botão de sacudir dá o mesmo
  impulso a todas as cadeias, para comparar ajustes, e só funciona no solver
  aproximado.
- **Parâmetros:** cada cadeia mostra `_Angle`, `_Stretch`, `_Squish`,
  `_IsGrabbed` e `_IsPosed`. O cartão também mostra uma contagem aproximada de
  transforms, colliders e collision checks.
- **Bloqueio:** com a simulação ligada, um objeto movido, girado ou escalado com
  as ferramentas da Scene View volta ao estado original ao soltar o mouse, sem
  deixar alteração na cena nem entrada de Undo.
- **PhysBone Colliders:** esfera, cápsula e plano; vermelho = *Inside Bounds*. O
  cartão lista cada collider com forma, tamanho, *Inside Bounds*, *Bones As
  Sphere* e quantos PhysBones o usam; clicar no nome seleciona o objeto. PhysBones
  desativados contam à parte, com aviso, e o tooltip mostra os nomes. Assim, **sem
  uso** quer dizer que nenhum PhysBone da cena referencia o collider.
- **Contacts:** senders (laranja) e receivers (azul; verde quando colidindo).
  A janela calcula colisões por tags, *Allow Self/Others* (avatar definido pelo
  `VRCAvatarDescriptor`) e mostra o valor de cada receiver: Constant, OnEnter
  (pulso de 0,25 s) e Proximity (0 na borda, 1 no centro; *Use Face
  Proximity* em caixas mede a partir da face +Z). Com **Advanced Parameters**
  ligado no receiver, a janela mostra `_SenderType`, `_SenderLocalPosition*`,
  `_SenderLossyScale*`, `_SenderVelocity*`, `_PointSeparation`,
  `_ProximitySpeed` e `_CollisionTag_*` do sender mais próximo. Com **Colliders
  do avatar** ligado, Head, Torso, Hand, Foot e Finger do Avatar Descriptor atuam
  como senders, como no Gesture Manager. Com **Tocar com o mouse** ligado, segurar
  o botão esquerdo sobre um receiver o aciona, com filtro de tag opcional; o toque
  não afeta Material Contacts.
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

- **EditorOnly:** objetos com essa tag, e tudo abaixo deles, são ignorados pela
  janela, porque o build os remove.
- **Busca:** filtra PhysBones e receivers pelo nome.
- **Tags de contacts:** mostra quantos senders e receivers usam cada tag e avisa
  quando uma tag não tem par na cena.
- **Verificar configuração:** aponta PhysBones sem efeito, ossos controlados por
  dois PhysBones, referências vazias, colliders sem uso, constraints sem fontes
  ou sem peso e ciclos entre constraints.
- **Atualizações do SDK:** a janela avisa quando um campo usado não existe mais.
- **Preferências:** ficam em `UserSettings/VrcEditModePreview.json`.

## Animator

Ligue **Animator** na barra do topo, ao lado de **Simular PhysBones**; ele liga a simulação junto, e ela
pode ser desligada depois. O primeiro cartão da janela mostra o que está em preview. Selecione um estado, uma blend
tree ou um clip na janela Animator, e a animação aparece no avatar da cena que usa aquele controller:

- **Full Controller do VRCFury:** os caminhos das animações são resolvidos como no build: a partir do
  objeto do Full Controller (ou do *Root Object Override*), subindo até a raiz do avatar, com as regras de
  *Rewrite Bindings*, *Root Bindings Apply To Avatar* e o ajuste da escala da raiz. Caminhos sem alvo
  aparecem no cartão **Propriedades**, porque o build também os descarta.
- **Avatar Descriptor:** camadas personalizadas (Base, Additive, Gesture, Action, FX e as especiais), com
  caminhos a partir da raiz do avatar.
- **Blend trees:** os parâmetros acompanham o slider da blend tree na janela Animator (desligue
  **Seguir Animator** para usar só os da janela), e podem ser arrastados no gráfico 1D ou 2D. O peso de cada
  clip é calculado pelo próprio Unity, para todos os tipos, inclusive Direct e blend trees aninhadas.
- **Músculos:** clips humanoides animam o corpo por cima da pose da cena, respeitando a máscara humanoide da
  layer; o quadril fica no lugar e root motion e IK ficam de fora.
- **Tempo:** tocar, pausar, arrastar a linha do tempo, velocidade e repetição. **Travar** mantém o preview
  atual ao selecionar outra coisa. Um `.anim` selecionado no Project usa o controller da cena que o toca.

Tudo passa pelo modo de animação do Unity, o mesmo do preview da janela Animation: os valores originais
ficam guardados, nada é salvo no arquivo da cena e a cena não fica modificada. Tudo volta ao parar, ao
salvar a cena (o preview volta logo depois), recompilar, fechar a cena, entrar no Play mode ou iniciar um
build do VRChat SDK. Se o Unity fechar no meio, a cena no disco continua como estava. Propriedades
animadas ficam vermelhas no Inspector, e edições nelas não ficam. Enquanto a janela Animation ou a
Timeline estiverem com o preview delas ligado, este espera.

Com **Simular PhysBones** ligado na janela VRChat Edit-Mode Preview, os dois rodam juntos na ordem do
VRChat: o animator escreve a pose e os PhysBones simulam por cima dela, inclusive em ossos de cadeia que a
animação move. Quando o preview anima músculos, a animação de teste dos PhysBones espera. A pose de repouso
da simulação é sempre lida sem o preview, então ligar e desligar os dois em qualquer ordem não muda nada no
avatar.

O preview mostra uma camada por vez, sem as outras layers, transições, write defaults, máscaras de
transform, *mirror* ou *cycle offset*. Desligue-o antes de bakes manuais de outras ferramentas que copiam o
avatar, como o **Build an Editor Test Copy** do VRCFury.

## Limites

É uma visualização: rede, permissões entre jogadores, parâmetros de animator e
colisão com as mãos de outros jogadores só existem no Play mode ou no cliente.
Confirme os valores finais lá.

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator
Companion e instale este pacote no projeto.
