# Changelog

Todas as mudanças relevantes deste pacote ficam neste arquivo. O formato segue o
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

## [0.9.0] - 2026-11-09

### Adicionado

- Interface em inglês. O idioma é escolhido uma vez para todos os pacotes do Dosker Tools, em **Tools > Dosker Tools > Language**, em **Preferences > Dosker Tools** ou no botão com o globo. Automático segue o idioma do sistema: fora de português, a interface já abre em inglês.

### Alterado

- Requer `com.dosker.editor-ui` 0.8.0.

### Removido

- O menu de validação. Ele era só para desenvolvimento e agora fica fora do pacote.

## [0.8.2] - 2026-11-07

### Corrigido

- Os **Advanced Parameters** dos receivers voltam a aparecer na interface. O SDK guarda a opção como flags, e a janela
  agora mostra só os parâmetros ativados, um por linha, com o nome que o animator recebe (`{parâmetro}_SenderType`,
  `_SenderVelocity`, `_CollisionTag_{tag}` etc.).

## [0.8.1] - 2026-11-02

### Alterado

- A aba **PhysBone Colliders** conta PhysBones desativados à parte, com aviso e os nomes no tooltip. **Sem uso** agora
  considera a cena inteira, mesmo com **Somente a hierarquia selecionada** ligado, e **Verificar configuração** avisa
  sobre colliders usados só por PhysBones desativados.

### Corrigido

- Objetos com a tag **EditorOnly**, e tudo abaixo deles, deixam de aparecer e de ser simulados, porque o build os remove.

## [0.8.0] - 2026-11-02

### Adicionado

- A simulação roda o `PhysBoneManager` do VRChat SDK no Edit mode, o mesmo solver do Play mode e do Gesture Manager. O
  status mostra **SDK** ou **Aproximada**; o solver aproximado continua como reserva se o SDK não puder iniciar.
- Colliders do Avatar Descriptor (Head, Torso, Hand, Foot e Finger) atuam como senders, com opção para desligar.
- **Tocar com o mouse:** segurar o botão esquerdo sobre um receiver o aciona, com filtro de tag opcional.
- A aba **PhysBone Colliders** lista cada collider com forma, tamanho, marcas e quantos PhysBones o usam.

### Alterado

- Os ossos são pegos pelo próprio osso na Scene View, e não mais por pontos clicáveis. Com o botão esquerdo pressionado,
  o direito posa, como no Play mode. **Grab Movement** e os filtros de Grab e Pose passam a valer.
- Solver aproximado: colisão testa o osso inteiro, com **Bones As Sphere**, e pode alterar o comprimento dentro de Max
  Stretch e Max Squish. Valem também Multi-Child, Ignore Other Phys Bones, as curvas de Stretch, Squish, Stretch Motion
  e Limit Rotation, e o limite de 256 transforms.
- Stretch e Squish movem os ossos, não só os gizmos.
- **Gravity Falloff** é medido em relação à pose de repouso, e `_Angle` usa o osso final.

### Corrigido

- A aba **PhysBone Colliders** abria sem conteúdo.

### Limitações

- Com o solver do SDK, a simulação pausa quando o Unity perde o foco, e o botão de sacudir fica desativado.

## [0.7.0] - 2026-11-01

### Adicionado

- Ossos de Armature Link do VRCFury acompanham o avatar durante a simulação.
- Animações de teste de movimento: parado, andar, correr, agachado, andar agachado, sentado e pular.
- T-pose e A-pose como sequências de alongamento e testes de pernas, tronco e braços, como levantar joelhos, agachamento,
  erguer os braços, abraçar o corpo e torcer o tronco.
- Stretch e Squish, com `_Stretch` e `_Squish`, e Allow Posing, com `_IsPosed`.
- Botão de sacudir, para comparar ajustes com o mesmo impulso.
- Contagem aproximada de transforms, colliders e collision checks.
- Busca, tabela de tags de contacts e verificação da configuração.
- Aviso de campos que não existem na versão instalada do SDK.
- Preferências em `UserSettings` e validação automatizada.

### Alterado

- Stiffness só vale em **Advanced**, e **Immobile World** desconta apenas o deslocamento do avatar.
- Requer `com.dosker.editor-ui` 0.7.0.

## [0.6.2] - 2026-10-31

### Removido

- Aviso sobre IA neste pacote. Ele fica apenas no README principal e na página do listing.

## [0.6.1] - 2026-10-31

### Adicionado

- Aviso de que o projeto foi criado com inteligência artificial.

## [0.6.0] - 2026-10-31

### Alterado

- Primeira versão como pacote VPM, separada do ShaderView.
- Menu movido para `Tools/Dosker Tools/VRChat Edit-Mode Preview`.
