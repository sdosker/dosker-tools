# Changelog

Todas as mudanças relevantes deste pacote ficam neste arquivo. O formato segue o
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

## [0.2.0] - 2026-11-09

### Adicionado

- Interface em inglês completa, incluindo a tabela. O idioma é escolhido uma vez para todos os pacotes do Dosker Tools, em **Tools > Dosker Tools > Language**, em **Preferences > Dosker Tools** ou no botão com o globo. Automático segue o idioma do sistema: fora de português, a interface já abre em inglês.

### Alterado

- Requer `com.dosker.editor-ui` 0.8.0.

### Corrigido

- Depois de uma recompilação com uma medição aberta, a janela lançava NullReferenceException e erros de layout. Agora ela mede de novo.

## [0.1.0] - 2026-11-01

### Adicionado

- Janela com o ranking de PC e Quest por categoria, separando Cena, Previsão e Real.
- Previsão calibrada pela diferença entre o avatar antes e depois do último build.
- Aviso quando não há build ou quando componentes do VRCFury mudaram desde ele.
- Lista das meshes mais pesadas.
