# Unity Open MCP

[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/unity-open-mcp/)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=white 'Unity')](https://unity.com/releases/editor/archive)
[![](https://img.shields.io/badge/Node.js-339933?style=flat&logo=nodedotjs&logoColor=white 'Node.js')](https://nodejs.org/en/download/)
[![](https://img.shields.io/github/stars/AlexeyPerov/Unity-Open-MCP 'Stars')](https://github.com/AlexeyPerov/Unity-Open-MCP/stargazers)
[![](https://img.shields.io/github/last-commit/AlexeyPerov/Unity-Open-MCP 'Last Commit')](https://github.com/AlexeyPerov/Unity-Open-MCP/commits/master)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

| [🇺🇸 English](README.md) | [🇨🇳 简体中文](README.zh-CN.md) | [🇷🇺 Русский](README.ru.md) |
|-------------------------|--------------------------------|------------------------------|

<p align="center">
  <img src="assets/brand/openmcp-symbol.svg" alt="" width="86">
  &nbsp;&nbsp;
  <img src="assets/brand/openmcp-wordmark-color.svg" alt="Open MCP" width="341">
</p>

Unity Open MCP — одна из самых широких по охвату open-source реализаций MCP для
Unity: **270+ типизированных инструментов** образуют стек автоматизации,
ориентированный на production. Безопасные изменения через шлюз, встроенная
валидация и выполнение в live, headless и offline режимах обеспечивают надёжную
работу с реальными проектами — от анализа ассетов и редактирования в Editor до
замкнутого тестирования игры, диагностики, CI и доменов Unity с пакетными
зависимостями.

---
Часть набора Open MCP
---
[![Unity Open MCP](https://img.shields.io/badge/Unity-Open%20MCP-000000?style=flat&logo=unity&logoColor=white)](https://github.com/AlexeyPerov/Unity-Open-MCP) [![Unreal Open MCP](https://img.shields.io/badge/Unreal-Open%20MCP-0E1128?style=flat&logo=unrealengine&logoColor=white)](https://github.com/AlexeyPerov/Unreal-Open-MCP) [![Godot Open MCP](https://img.shields.io/badge/Godot-Open%20MCP-478CBF?style=flat&logo=godotengine&logoColor=white)](https://github.com/AlexeyPerov/Godot-Open-MCP)
---

## Ключевые возможности

### Безопасное типизированное редактирование

Работайте с GameObject, сценами, префабами, материалами, пакетами и доменами с
зависимостями — NavMesh, Input System, Cinemachine, Timeline, Shader Graph и
другими. Изменения проходят `checkpoint → mutate → validate → delta`, с
регрессионными проверками и точечными исправлениями.

> **Пользователь:** Удали этот префаб.<br>
> **Агент:** Превью шлюза нашло новые missing references в `Level1` и
> `SpawnPoint`, поэтому я остановился до повреждения проекта.

### Плейтест игры глазами тестировщика

Агент может сыграть в запущенную игру и вернуть отчёт, который удобно
просмотреть человеку. Он выставляет целевое разрешение или устройство Device
Simulator, читает экран (тексты uGUI, видимость, диагностику вёрстки), нажимает
элементы по надписи на экране, кликает, перетаскивает, свайпает и проигрывает
многосекундные жесты на реальных кадрах (и в проектах с legacy Input Manager, и
с Input System), а затем ждёт, пока игра дойдёт до нужного состояния — строки в
логе, надписи, объекта или собственного предиката проекта, при желании с
ускорением времени. Снимки делаются в нативном разрешении устройства с учётом
safe area или сериями по расписанию кадров для анимаций. Сохранение игрока
(PlayerPrefs и `persistentDataPath`) снимается и восстанавливается, чтобы сессия
не тратила прогресс разработчика, а весь прогон — шаги, вердикты, кадры и ошибки
консоли, вызванные каждым шагом, — записывается в Markdown-отчёт.

> **Пользователь:** Прогони смоук-тест магазина на iPhone 13.<br>
> **Агент:** Пять шагов прошли; нажатие *Buy* выбросило `NullReferenceException`.
> В отчёте есть кадры и стек. Твоё сохранение не тронуто.

Подробнее: [Руководство по плейтесту](docs/playtest-guide.md) и
[Инструменты плейтеста](docs/api/playtest.md) (англ.).

### Тестирование и наблюдение

Запускайте Edit/Play Mode тесты, читайте консоль, делайте скриншоты, снимки
profiler и памяти, получайте события — обратная связь, по которой агент может
доверять собственным изменениям.

### Live, batch или offline

Предпочтителен живой Editor; поддерживаемые инструменты могут перейти на
headless Editor точной версии, а ассеты и диагностику компиляции можно читать с
диска при закрытой Unity. Структурированный поиск, пересериализация и анализ
ссылок / зависимостей доступны там, где это допускает маршрут.

### Расширение и автоматизация

Проекты Unity могут публиковать типизированные обнаруживаемые команды без нового
релиза MCP-сервера. Явно асинхронные операции выполняются как наблюдаемые задачи
с прогрессом, идемпотентностью, сохранённым результатом и честной отменой. CLI и
CI добавляют health-check, verify-baseline и регрессионные шлюзы.

Подробнее: [Команды проекта](docs/api/project-commands.md) и
[Асинхронные задачи](docs/api/jobs.md) (англ.).

### Только нужные инструменты

Сессия начинается с групп `core` и `gate-and-verify` и небольшого набора
всегда видимых мета-инструментов; группы Shader Graph, VFX Graph и Memory
Profiler включаются автоматически, если найден их пакет, а остальные домены
активируются по запросу. Runtime capabilities возвращают точные схемы, маршруты
и доступность, а скилы проекта учат агентов циклу mutate → gate → fix.

### Установка и сопровождение

Одна команда устанавливает согласованные версии пакетов, MCP-конфигурацию и
агентский скил. Командные и monorepo-layout по умолчанию получают переносимую
машинно-независимую конфигурацию. Согласованные сценарии обновляют MCP-сервер,
Unity-пакеты и опциональное приложение [Unity Hub Pro](docs/unity-hub-pro.md).

Больше примеров промптов: [docs/api/mcp-tools.md](docs/api/mcp-tools.md#example-prompts)
(англ.). Полный каталог и контракты — в
[docs/api/mcp-tools.md](docs/api/mcp-tools.md) (англ.).

## Быстрая установка

Требуются **Unity 2022.3 LTS или новее** и **Node.js 18+** для MCP-сервера.

1. **CLI — рекомендуется:** откройте терминал в проекте Unity и выполните:

   ```bash
   npx -y unity-open-mcp@latest setup --client cursor
   ```

   В монорепозитории запустите её из корня репозитория и назовите папку Unity:
   `setup --client cursor --unity-subpath Client`. Вместо `cursor` можно
   использовать `claude`, `zcode`, `vscode`, `codex`, `opencode` или `agents`.
   Команда установит согласованные pins, конфигурацию проекта и встроенный скил.
   Из корня репозитория она пишет конфигурацию без машинных путей, которую
   команда может коммитить, — см.
   [Переносимую конфигурацию MCP](docs/ru/setup/portable-config.md).
2. **Unity Hub Pro:** используйте графический поток из
   [Установки через мастер](docs/ru/setup/wizard-setup.md).
3. **Вручную:** добавьте пакеты Unity по [Ручной установке](docs/ru/setup/manual-setup.md),
   затем запись клиента из [Конфигурации MCP-клиента](docs/ru/setup/client-configuration.md).
4. **Локальный чекаут:** соберите и запустите репозиторий с помощью
   [Установки для разработки](docs/ru/setup/development-setup.md).
5. **Экспериментально — ИИ-агент:** вставьте промпт ниже в ваш ИИ-клиент.

<details>
<summary>Промпт для установки агентом</summary>

```text
Установите Unity Open MCP в этот проект Unity, точно следуя
https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/agent-setup.md
(загрузите процедуру заново; не импровизируйте по памяти).
Определите мой клиент и корень проекта Unity, затем выполните шаг 2 этой страницы:
npx -y unity-open-mcp@latest setup --project <абсолютный-проект-unity> --client <id>
или, если мой клиент открыт на папке выше проекта Unity (монорепозиторий),
из этой папки: npx -y unity-open-mcp@latest setup --client <id> --unity-subpath <относительная-папка-unity>
Пусть этот пакет выберет все версии и скопирует встроенный SKILL.md; не
выдумывайте версии, не переписывайте скил и не вызывайте generate_skill.
Выполняйте шаги самостоятельно и остановитесь только для действия пользователя.
Если доступен локальный чекаут репозитория Unity-Open-MCP, читайте
docs/setup/agent-setup.md с диска вместо загрузки.
```

</details>

Полная процедура: [Установка агентом](docs/ru/setup/agent-setup.md).
Для команд и монорепозиториев используйте
[переносимую конфигурацию MCP](docs/ru/setup/portable-config.md), которую можно
хранить в репозитории без абсолютных путей разработчиков.

## Документация

Для пользователей:

- [Индекс API](docs/api.md) (англ.) — контракты MCP, моста, ресурсов, маршрутизации и автоматизации.
- [Расширения](docs/extensions.md) (англ.) — встроенные домены, зависимости и активация групп инструментов.
- [Устранение неполадок](docs/troubleshooting.md) (англ.) — диагностика подключения и восстановления.
- [Политика диалогов](docs/dialog-policy.md) (англ.) — обработка стартовых модальных окон и автоматизация.
- [Скилы](docs/skills.md) (англ.) — агентские плейбуки, устанавливаемые в проекты Unity.
- [Совместимость версий](docs/ru/versioning.md) — согласование версий и восстановление при рассинхроне.
- [Обновление](docs/updating.md) (англ.) — обновление Hub, MCP и Unity-пакетов, включая изолированные среды.

Для контрибьюторов:

- [Архитектура](docs/architecture.md) (англ.) — границы репозитория и поток выполнения.
- [Соглашения по коду](docs/code-conventions.md) (англ.) — неочевидные C# контракты.

> Хотите посмотреть другие MCP-решения? Смотрите [сравнение MCP-инструментов для
> Unity](docs/mcp-tools-comparison.md) (англ.) — параллельная матрица возможностей
> Unity Open MCP и других MCP-инструментов / ИИ-ассистентов в этой области.

> Примечание: помимо этого README, документов установки в `docs/ru/setup/` и
> страницы совместимости версий, остальная документация пока доступна только на
> английском.

## Контрибьюция

- Перед созданием issue или pull request прочитайте
  [CONTRIBUTING.md](CONTRIBUTING.md) (англ.).
- [Устранение неполадок для контрибьюторов](docs/troubleshooting-contributors.md)
  (англ.) охватывает локальные тесты, сбои моста и автоматизации.
- [Версионирование и релизы для мейнтейнеров](docs/contributing/versioning.md)
  (англ.) — синхронизация, теги и релизные процессы.

**Лицензия:** MIT — см. [LICENSE](LICENSE).
