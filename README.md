> [!CAUTION]
> # ⚠️ AVISO: IA ⚠️
>
> ## TODAS FERRAMENTAS DISPONIBILIZADAS AQUI FIZERAM USO DE IA NA SUA CRIAÇÃO
>
> **O código, os scripts e a documentação foram escritos com auxilio inteligência artificial, sob revisão humana.**

# Dosker Tools

[Read this in English](README.en.md)

Ferramentas de Editor para criadores de VRChat, distribuídas como pacotes VPM para o
[VRChat Creator Companion](https://vcc.docs.vrchat.com/) (VCC).

## Instalar

1. Abra a página do listing: <https://sdosker.github.io/dosker-tools/>.
2. Clique em **Add to VCC**. Se preferir, adicione manualmente no VCC, em
   **Settings > Packages > Add Repository**, este endereço:

   ```text
   https://sdosker.github.io/dosker-tools/index.json
   ```

3. No VCC, abra **Manage Project** e adicione os pacotes desejados.

As janelas ficam no menu **Tools > Dosker Tools**.

## Idioma

Todas as janelas estão em português e em inglês. Por padrão, seguem o idioma do sistema. Para trocar, use
**Tools > Dosker Tools > Language**, **Edit > Preferences > Dosker Tools** ou o botão com o globo em qualquer
janela do Dosker. A escolha vale para todos os pacotes instalados de uma vez.

## Pacotes

| Pacote | O que faz | Licença |
|---|---|---|
| [ShaderView](Packages/com.dosker.shader-view/README.md) | Depura shaders simulando as câmeras e os contextos do VRChat | MIT |
| [VRChat Edit-Mode Preview](Packages/com.dosker.vrc-edit-mode-preview/README.md) | Visualiza PhysBones, Contacts e Constraints fora do Play mode | MIT |
| [Animation Audio Waveform](Packages/com.dosker.animation-audio-waveform/README.md) | Mostra o áudio dentro da janela Animation | MIT |
| [Compressor Exclusions](Packages/com.dosker.compressor-exclusions/README.md) | Escolhe quais parâmetros o compressor do VRCFury pode comprimir e prevê o resultado do build | [Não comercial](Packages/com.dosker.compressor-exclusions/LICENSE.md) |
| [Performance Budget](Packages/com.dosker.performance-budget/README.md) | Prevê o ranking do avatar considerando o que o VRCFury muda no build | MIT |
| [Editor UI](Packages/com.dosker.editor-ui/README.md) | Interface compartilhada, instalada como dependência | MIT |
| [Todas as ferramentas](Packages/com.dosker.all/README.md) | Instala todos os pacotes de uma vez | Ver cada pacote |

O Compressor Exclusions também precisa do VRCFury, que deve ser instalado pelo
[listing oficial](https://vcc.vrcfury.com).

## Migrar de um `.unitypackage`

Ao instalar pelo VCC, os arquivos antigos em `Assets/Editor` são removidos automaticamente, e outros
scripts dessa pasta não são tocados.

Se você importou o Compressor Exclusions por `.unitypackage`, apague os scripts da pasta antiga dele
em `Assets` antes de instalar. Mantenha o `CompressorExclusions.asset`: a configuração continua
valendo no lugar onde está.

## Desenvolvimento

Este repositório não é um projeto Unity: ele guarda só os pacotes, em `Packages/`, e a automação de
publicação. Para testar ou gerar `.meta`, copie a pasta do pacote para `Packages/` de um projeto de
avatar com o VRChat SDK.

Cada pacote tem a própria versão no formato `A.B.C`, e só muda o pacote que mudou: **A** representa
mudanças gigantes, **B** mudanças consideráveis e **C** mudanças pequenas ou correções. Ao incrementar um
campo, zere os campos à sua direita. O Compressor Exclusions segue as regras do próprio `CHANGELOG.md`.

Para testar em um projeto de avatar:

```powershell
python scripts/sync_to_project.py push <projeto>     # copia os pacotes
python scripts/compile_check.py <projeto>            # compila sem abrir o Unity
python scripts/sync_to_project.py pull <projeto>     # traz os .meta gerados
dotnet run --project tests/CompressorEstimator.Tests
python -m unittest discover -s tests -p "test_*.py"
```

A validação que só roda dentro do Unity fica em [tests/Unity](tests/Unity/README.md), fora dos pacotes
publicados. Ela entra no projeto de teste com `--tests` no `push` e no `compile_check.py`.

O workflow **VRCFury compatibility** abre uma issue quando uma versão nova muda os arquivos usados pelo
Compressor Exclusions. Depois de revisar, registre com `python scripts/check_vrcfury.py --update` e
atualize `TestedVrcfury` no código. O workflow **Unity smoke test** roda à mão, em um runner próprio com a
etiqueta `unity`.

### Lançar uma versão

1. Rode `python scripts/bump.py <pacote> <major|minor|patch>` e preencha a entrada do `CHANGELOG.md`.
2. Traga de volta o `.meta` que o Unity gerou para cada arquivo novo. Nunca crie um `.meta` à mão.
3. Confira localmente com `python scripts/build_packages.py`.
4. Envie para a `main`. O GitHub Actions cria a release de cada versão nova e atualiza o listing.
