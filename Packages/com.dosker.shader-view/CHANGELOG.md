# Changelog

Todas as mudanças relevantes deste pacote ficam neste arquivo. O formato segue o
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

## [0.7.0] - 2026-11-01

### Adicionado

- Configurações salvas em `UserSettings` e perfis com nome.
- Luz ambiente e globais extras aplicadas só durante a captura.
- Lista das globais do VRChat citadas pelo shader.
- Tempo e memória da última captura.
- Faixas de FOV aproximadas na aba estéreo.
- Botão para usar a seleção como alvo e item de menu para validar.

### Alterado

- A aba **IF pimax** passou a se chamar **Estéreo**.
- Abrir a janela não troca mais de cena sozinho.
- Requer `com.dosker.editor-ui` 0.7.0.

### Corrigido

- O título mostra a versão real do pacote.
- **Create exact Unity preview** funciona em um projeto novo, sem a pasta `Assets/ShaderView`.

## [0.6.2] - 2026-10-31

### Removido

- Aviso sobre IA neste pacote. Ele fica apenas no README principal e na página do listing.

## [0.6.1] - 2026-10-31

### Adicionado

- Aviso de que o projeto foi criado com inteligência artificial.

## [0.6.0] - 2026-10-31

### Alterado

- Primeira versão como pacote VPM, instalável pelo VRChat Creator Companion.
- Menus movidos para `Tools/Dosker Tools/ShaderView`.
- A cena de teste deixa de ser distribuída: ela é criada pelo menu **Create exact Unity preview**.
