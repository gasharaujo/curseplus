# Validação — 24/10/2026

Ambiente: Windows x64, SDK .NET 10.0.402, WPF `net10.0-windows`, FlaUI.UIA3 5.0.0 e CurseForge standalone **1.324.0.41180**, interface em inglês.

## Verificações automatizadas

- Compilação Release com avisos tratados como erros: zero avisos e erros.
- **77/77 testes aprovados**: leitura, comparação, mescla, versões, ativação, escopo, integridade ZIP, snapshots, recuperação, exportação incremental e executor com adaptador simulado. Inclui metadados transitórios, confirmação de JARs físicos, manifesto original e ausência de `manifest.version` após atualização nativa.
- A captura completa histórica de FTB Evolution 2 leu **429 mods gerenciados e 2.797 arquivos personalizados**. Esse resultado de 83.709.332 bytes era um snapshot completo; não representa mais a exportação padrão.
- Cinco abas do CursePlus renderizadas; executável portátil com runtime incluído, documentação e licenças.

## Exportação leve — 24/10/2026

- Formato versão 2, com apenas diferenças e assinatura do original. JSON compacto para alterações sem anexos; ZIP com caminhos `files/...` para configurações, scripts e JARs locais/modificados realmente diferentes. Metadados e conteúdos originais não são enviados no arquivo exportado.
- Teste controlado: original com **429 mods e 2.797 referências de arquivos**, cada arquivo representando 1 MiB de assets. Ao adicionar um único mod, a exportação gerou **653 bytes**, sem ler ou criar nenhum desses assets. O teste usa arquivos inexistentes deliberadamente para comprovar que a gravação não os acessa. O tamanho não é uma medida de uma exportação real do FTB.
- Caso real salvo nos testes nativos anteriores: `native-pack-customized.curseplus.zip` tinha **10.108 bytes**. Reexportado como diferenças contra `native-pack-original.curseplus.zip`, gerou **1.103 bytes**, com dois mods alterados, uma configuração anexada e dez arquivos originais excluídos do envio. O estado reconstruído foi comparado com o snapshot completo e coincidiu. Resultado em `.artifacts/native-pack-changes-v2.curseplus.zip`.
- Os novos testes cobrem aplicação a partir do original, instalação por adaptador nativo, remoções, mescla após reset de versão do pack, preservação de bytes do destino na retomada, original incorreto/ausente, JAR modificado, rejeição de escrita direta de JAR normal, corrupção, caminhos inválidos e anexos não referenciados.
- Leitura e cópia dos conteúdos usam streaming; buffers grandes são reutilizados. O original é consultado por metadados ao exportar, sem descompactar seus assets. A resolução do pacote não cria um ZIP completo intermediário. Não há monitoramento ou compactação em segundo plano.
- Backups, temporários e logs reconhecidos são excluídos da captura. Assets legítimos, mesmo imagens/áudios, só são anexados quando alterados ou adicionados.
- Os pacotes completos versão 1 continuam sendo aceitos. As mudanças desta atualização foram testadas sem manipular a tela do usuário; não foi repetida a validação da UI do CurseForge para o novo formato.

## Testes reais pelo CurseForge — 23/10/2026

Todas as mutações usaram perfis criados para o teste, com nomes iniciados por `CursePlus Validation`. As oito instâncias anteriores do usuário não foram destinos de operações. Os seis perfis descartáveis foram removidos ao fim usando **Delete Profile** do CurseForge; snapshots e diagnósticos de desenvolvimento permanecem em `.artifacts/`, fora da distribuição.

| Operação | Resultado observado |
| --- | --- |
| Criar/importar perfil próprio | Perfil Forge 1.20.1 importado pelo diálogo ZIP nativo, com IDs e arquivos instalados confirmados. |
| Instalar versão exata | FastBoot, projeto 1030285, arquivo 5696829; instalação pelo protocolo nativo e conferência posterior. |
| Desativar, remover e restaurar mod | FastBoot desativado, removido com confirmação nativa e reinstalado/desativado por restauração do snapshot. |
| Restaurar snapshot em novo perfil próprio | Novo perfil importado; estado de ativação restaurado e comparação final aprovada. |
| Quick add compatível | EdivadLib 638508 resolveu arquivo 4717006 em Forge 1.20.1; Mouse Tweaks 60089 resolveu 5230338 em Forge 1.20.4. IDs do projeto enviados sem `fileId`. |
| Quick add incompatível | Fabric API 306612 recusado pelo seletor nativo em Forge 1.20.1; resultado incompatível, sem instalação. |
| Versão do loader | Forge 47.4.0 → 47.4.10 pelas opções nativas do perfil. |
| Instalar modpack publicado | Vanilla Forge, projeto 238298, arquivo 5298522; perfil instalado pelo CurseForge e renomeado nativamente. |
| Atualizar versão do pack | 5298522 → 5421216; o CurseForge alinhou Minecraft 1.20.4/Forge 49.0.30 para Minecraft 1.20.6/Forge 50.0.8. |
| Original limpo em novo perfil | Mesma versão 5298522 reinstalada com **Update to a separate modpack profile**. Comparação com original: 8 mods e 10 configurações. Origem com mod extra, mod desativado e configuração adicional permaneceu igual, inclusive a preferência de gerenciamento de conteúdo. |
| Sincronização completa | Executor mudou nativamente 5421216 → 5298522, instalou Mouse Tweaks exato, desativou AppleSkin e aplicou a configuração de teste. Journal concluído e comparação integral aprovada. |
| Restaurar original no mesmo perfil | Removeu Mouse Tweaks nativamente, reativou AppleSkin e removeu somente a configuração de teste. Journal concluído e comparação integral aprovada. |

A configuração de teste `config/curseplus-validation.json` foi aplicada/removida pelo mecanismo de arquivos personalizados, exceção permitida para operações por arquivo que o CurseForge não oferece. Não houve escrita direta em JARs gerenciados normais, metadados ou banco do CurseForge.

## Limites reais

- Beta dependente da interface do CurseForge. Português tem aliases, mas não foi testado de ponta a ponta. Fabric, NeoForge e Quilt não tiveram instalação real validada.
- Não foram iniciados jogos nem verificadas compatibilidade de gameplay, saves ou execução de scripts. Mundos e demais conteúdos fora do escopo não são sincronizados.
- Mescla, recuperação de journals, arquivos locais/modificados e segurança de caminhos foram testados de forma automatizada; não houve teste real de todos esses casos contra o CurseForge.
- A criação de original limpo para uma versão já instalada depende do diálogo nativo de mudança de versão e da opção de perfil separado. Packs que não disponibilizem esse fluxo podem falhar; não existe instalação externa de contorno.
- Arquivos removidos do catálogo, falhas de rede, downloads incompletos e mudanças de layout podem impedir aplicação/restauração. A operação registra o ponto anterior; não há rollback nativo atômico. Downloads já iniciados pelo CurseForge podem continuar após interrupção.
- O adaptador confirma identidade dos perfis, IDs/arquivos e estado final. Controles ambíguos, cobertos ou telas inesperadas interrompem a ação. Nenhum resultado é apresentado como concluído sem a comparação final.

## Reproduzir em desenvolvimento

Execute `./build.ps1` para compilar e rodar a suíte; `./build.ps1 -Publish` atualiza a distribuição. `CursePlus.exe --smoke pasta` renderiza as cinco abas com armazenamento isolado, e `--inspect-ui caminho.json` coleta o diagnóstico somente de leitura.

O comando de desenvolvimento `--native-test especificacao.json resultado.json` executa um caso com timeout, trace, diagnóstico UIA e captura da janela. Ele restringe perfis mutantes ao prefixo de validação. As especificações/resultados desta sessão estão em `.artifacts/native-*.json`; os journals completos ficam em `.artifacts/native-data/operations/`. Alguns resultados iniciais registram falhas que motivaram correções; a sincronização final está em `native-full-sync-retry-3-result.json`, a restauração em `native-original-restore-final-result.json` e quick add em `native-quick-positive-final-result.json` e `native-quick-incompatible-final2-result.json`.

`CursePlus.exe --verify-package entrada saida.json` valida um arquivo sem abrir a janela do app nem controlar o CurseForge. Para reproduzir a compactação do caso salvo: execute a suíte com `--compact-export original.zip estado-completo.zip alteracoes.zip`. A medição controlada da suíte fica em `tests/CursePlus.Tests/bin/Release/net10.0/light-export-benchmark.json`.
