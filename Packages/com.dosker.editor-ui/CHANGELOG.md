# Changelog

Todas as mudanças relevantes deste pacote ficam neste arquivo. O formato segue o
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

## [0.8.0] - 2026-11-09

### Adicionado

- Menu **Tools > Dosker Tools > Language** e página **Preferences > Dosker Tools** para escolher o idioma de todos os pacotes de uma vez.
- `DoskerEditorUi.LanguageButton`, botão com o globo e a sigla do idioma, e `DoskerLocale.ShowMenu`.
- `DoskerLocale.Changed`, avisado quando o idioma muda. Todas as janelas são redesenhadas na hora.

### Alterado

- Os idiomas aparecem com o nome no próprio idioma (English, Português), para quem não lê o idioma atual achar o seu.
- `DoskerLanguage` passou a ter nomes em inglês: `Automatic`, `Portuguese` e `English`. A escolha salva continua valendo.

## [0.7.0] - 2026-11-01

### Adicionado

- Abas, divisor, banner com ação, estado vazio, busca, lista virtualizada, cabeçalho de tabela, campo com "voltar ao padrão", aviso passageiro e rodapé "Sobre".
- `DoskerSettings<T>`, para preferências em `UserSettings`.
- `DoskerLocale`, com tradução para inglês e escolha de idioma.
- Medidas públicas de espaçamento e raio.
- Galeria de componentes para desenvolvimento, com o define `DOSKER_UI_GALLERY`.
- Símbolo nos indicadores de estado, além da cor.
- Foco e ativação por teclado em cartões, chips e botões.

### Alterado

- `DoskerEditorUi` passou para o namespace `DoskerTools.EditorUi`. Quem usa o pacote precisa de `using DoskerTools.EditorUi`.

### Corrigido

- `PrimaryButton` atualiza ao passar o mouse em janelas com `wantsMouseMove`.
- Componentes não alteram mais a cor de estilos compartilhados.
- Menos alocações por frame em badges e indicadores.

## [0.6.2] - 2026-10-31

### Removido

- Aviso sobre IA neste pacote. Ele fica apenas no README principal e na página do listing.

## [0.6.1] - 2026-10-31

### Adicionado

- Aviso de que o projeto foi criado com inteligência artificial.

## [0.6.0] - 2026-10-31

### Alterado

- Primeira versão como pacote VPM, separada do ShaderView.
- `ShaderViewEditorUi` passa a se chamar `DoskerEditorUi` e a ser pública.
