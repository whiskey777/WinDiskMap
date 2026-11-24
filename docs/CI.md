# Непрерывные проверки (Quality Gate)

Все проверки ниже бесплатны для публичного репозитория и включены в `.github/`.

## Что уже работает в Actions

| Workflow | Когда запускается | Что проверяет |
|---|---|---|
| `ci.yml` → `build + smoke test` | push в `main`, PR в `main`, вручную | сборку `net8.0` + `net48` и экспорт CSV через `WinDiskMap.exe --no-mft --headless` |
| `ci.yml` → `dependency review` | только PR | новые/изменённые зависимости на известные уязвимости; порог `moderate`, комментарий в PR при провале |
| `codeql.yml` → `analyze (csharp)` | push в `main`, PR в `main`, по понедельникам, вручную | статический анализ C#: `security-extended` + `security-and-quality` |
| `release.yml` | тег `v*` или вручную | сборку архивов, при наличии SignPath — подпись, публикацию pre-release |

Все внешние actions закреплены по SHA (`@<40 hex> # vX`) — это защищает цепочку сборки от
подмены тега. Dependabot (`.github/dependabot.yml`) раз в неделю проверяет только actions.

## Что нужно включить в настройках репозитория (бесплатно)

1. **Settings → Code security and analysis**
   - Dependency graph — включить (обычно включается автоматически).
   - Dependabot alerts / security updates — включить.
   - Secret scanning — включить.
   - Secret scanning push protection — включить (блокирует коммит с секретом до отправки).
2. **Settings → Branches → Branch protection rules** для `main`:
   - Require a pull request before merging: 1 approval, dismiss stale approvals.
   - Require status checks to pass: `build + smoke test`, `analyze (csharp)` и
     `dependency review` (последний появится после первого PR).
   - Require branches to be up to date before merging.
   - Require conversation resolution before merging.
   - Block force pushes and branch deletion.
   - Include administrators — опционально, но рекомендуется.
3. **Settings → Actions → General**
   - Workflow permissions: `Read repository contents and packages` (по умолчанию уже так).
   - Allow GitHub Actions to create and approve pull requests — выключить, если не используем
     авто-обновления от Actions.
4. **Settings → General → Pull Requests**
   - Allow squash merging — включить, чтобы история `main` оставалась линейной.
   - Automatically delete head branches — включить.

## Локальный прогон перед PR

```powershell
.\scripts\build.ps1
.\scripts\package.ps1
```

CodeQL локально не запускается (нужен Actions), но его проверки дублируют обычную сборку —
достаточно зелёного `build + smoke test`. Если PR добавляет `PackageReference`, заранее проверьте,
что пакет не находится в списке уязвимостей: Dependency Review всё равно остановит merge.

## Как читать результаты

- **CodeQL alert** — не автоматический приговор. Откройте alphanumeric ID в Security → Code
  scanning, проверьте путь данных. Ложные срабатывания закрывайте как `false positive` с
  пояснением, а не отключением всего ruleset'а.
- **Dependency Review** — сравнивает dependency graph базовой ветки и PR. Обновление ради
  обновления без устранения уязвимости не требуется; Dependabot создаст отдельный PR.
- **Smoke test** — минимальная гарантия, что exe запускается без прав администратора и пишет
  корректную шапку CSV. Полная проверка точности — [BENCHMARK.md](BENCHMARK.md).

## Почему именно так

- Публичный репозиторий получает CodeQL, Secret scanning и Dependabot бесплатно; платный GitHub
  Advanced Security здесь не нужен.
- Job'ы разделены: у CodeQL нужен `security-events: write`, у сборки — только `contents: read`,
  у Dependency Review — `pull-requests: read`. Минимальные права = меньше последствий от компрометации
  action'а.
- Пиннинг по SHA и отдельный `release.yml` с ручным одобрением signing-запроса нужны также для
  заявки в SignPath (см. [BUILD.md](BUILD.md#подпись-exe)).
