# Changelog

Todas as mudanças relevantes deste pacote ficam neste arquivo. O formato segue o
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

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
