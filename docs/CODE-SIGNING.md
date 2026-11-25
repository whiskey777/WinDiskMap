# Code signing policy / Политика подписи кода

## 1. Проект

| Поле | Значение |
|---|---|
| Название | WinDiskMap |
| Репозиторий | https://github.com/whiskey777/WinDiskMap (публичный) |
| Лицензия | MIT (`LICENSE`), Copyright © 2026 Andrei Korneev |
| Тип | Открытый проект (OSS), консольный анализатор диска для Windows |
| Владелец / maintainer | Andrei Korneev |
| Система сборки | GitHub Actions, `windows-latest`, только hosted runners |
| Сервис подписи | SignPath Foundation (бесплатная подпись для OSS) |

Проект отвечает требованиям SignPath Foundation: публичный репозиторий, OSI-лицензия,
активная разработка, документация, проверки в CI и разделение ролей при выпуске релиза.

## 2. Область подписи

Подписываются **только исполняемые файлы** внутри двух portable-архивов:

| Артефакт | Подписываемый файл |
|---|---|
| `WinDiskMap-portable-net48-v<version>.zip` | `WinDiskMap.exe` (.NET Framework 4.8) |
| `WinDiskMap-portable-win-x64-selfcontained-v<version>.zip` | `WinDiskMap.exe` (net8.0, single-file) |

Текстовые файлы (`README.txt`, `LICENSE.txt`, `SHA256SUMS.txt`) не подписываются отдельно:
их целостность проверяется по `SHA256SUMS.txt` внутри архива. Хэши готовых ZIP-архивов
публикуются в `RELEASE_SHA256SUMS.txt` при каждом релизе.

Конфигурация артефакта SignPath хранится в `signpath/artifact-configuration.xml` и должна быть
скопирована в SignPath → Project → Artifact Configurations. Параметры запроса:

| Параметр | Пример | Откуда берётся |
|---|---|---|
| `version` | `1.0.0` | тег `v1.0.0` без префикса `v`; совпадает с `<Version>` в csproj |
| `fileVersion` | `1.0.0.0` | `version` без суффикса + `.0`; записывается в PE-ресурс |

Другие файлы, включая `.dll`, скрипты и конфигурации, подписи не требуют: в archivах их нет
(кроме `WinDiskMap.exe.config`, который не исполняется).

## 3. Роли

| Роль | Кто | Обязанности |
|---|---|---|
| Submitter | maintainer, создающий тег релиза | проверяет зелёный CI, создаёт/отправляет тег, инициирует signing-запрос из `release.yml` |
| Approver | второй участник проекта с ролью Approver в SignPath | сверяет тег, csproj, CHANGELOG и хэши, одобряет или отклоняет запрос |
| Auditor | любой maintainer | читает журнал SignPath и историю Actions |

API-токен `SIGNPATH_API_TOKEN` хранится в GitHub Actions secrets и не попадает в логи.
Значения `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`
и `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` хранятся в Variables — они не являются секретами.

Приватные ключи и сертификаты **не хранятся** в репозитории, на рабочих станциях и в runners:
подпись выполняется в HSM SignPath, сертификат выпускается на имя SignPath Foundation.

## 4. Процесс выпуска

1. Изменения идут через PR в `main`. Обязательны: сам PR, зелёные
   `build + smoke test`, `analyze (csharp)` и `dependency review`, отсутствие неразрешённых
   диалогов. Одобрение review не требуется (`required_approving_review_count: 0`) — это
   выбрано для одного maintainer'а. См. [CI.md](CI.md).
2. Релизный тег `v<version>` создаётся только из зелёного `main`. Workflow `release.yml`
   проверяет, что `<Version>` в csproj равен тегу, собирает оба архива из этого коммита и
   загружает их как GitHub Actions artifact.
3. SignPath проверяет origin через GitHub connector: репозиторий, коммит, workflow,
   github-hosted runner и то, что подписываемый artifact действительно создан этим workflow.
   Все actions закреплены по SHA, а не по плавающим тегам.
4. При настроенных `SIGNPATH_*` action отправляет signing-запрос с параметрами `version` и
   `fileVersion` и ждёт одобрения (`wait-for-completion: true`).
5. Approver сверяет данные запроса с тегом и CHANGELOG, затем одобряет. При малейшем
   подозрении запрос отклоняется — релиз не публикуется.
6. Подписанные архивы извлекаются в `artifacts/signed`, для них считается
   `RELEASE_SHA256SUMS.txt`, и публикуется GitHub-релиз. Статус `pre-release` выставляется
   только для версий с суффиксом (например `1.0.1-rc.1`): с `1.0.0` проект вышел из beta.
7. Если SignPath не настроен, workflow публикует неподписанные архивы с явным указанием этого
   в примечаниях к релизу. Для корпоративного распространения проверьте подпись или согласуйте
   хэши из `RELEASE_SHA256SUMS.txt` со службой безопасности.

## 5. Проверка подписи

```powershell
# После распаковки архива из Releases
Get-AuthenticodeSignature .\WinDiskMap.exe |
    Format-List Status, StatusMessage, SignerCertificate

# Сверка хэшей внутри архива
Get-Content .\SHA256SUMS.txt
Get-FileHash .\WinDiskMap.exe -Algorithm SHA256
```

Ожидаемый `Status`: `Valid`. Субъект сертификата — SignPath Foundation. Если статус
`NotSigned`, это сборка из исходников или SignPath был не настроен.

## 6. Отзыв и инциденты

- При обнаружении уязвимости в подписанной версии: удалить/скрыть релиз, отозвать тег,
  выпустить исправление с новой версией и пометить старый хэш в CHANGELOG.
- При утечке `SIGNPATH_API_TOKEN`: немедленно отозвать secret в настройках репозитория,
  выпустить новый, проверить журнал signing-запросов за период утечки.
- При подозрении на компрометацию цепочки сборки: проверить SHA-пиннинг actions и историю
  workflow, при необходимости отозвать сертификат через SignPath.

## 7. Изменение политики

Изменения этого файла, `signpath/artifact-configuration.xml` и `release.yml` принимаются
только через PR с ревью. После изменения политики нужно обновить соответствующие записи в
SignPath и повторно прогнать тестовый релиз по шагам 2–6.
