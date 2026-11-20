# Главное меню Inventory в Clayzor

## Архитектура

Поток данных и отображения:

`Home.razor` → `InventoryMainMenu` → `InventoryMenuData` (Entities, read-only `Запросы`) → `InventoryMainMenuComposer` + `InventoryMenuUrlBuilder` → `ClayNavigationMenu` (Controls).

- `ClayNavigationMenu` не знает Inventory, SQL, `Запросы` или правила URL и пригодна для других приложений.
- `InventoryMenuData` использует существующий scoped `DbManager`/`DynamicSql`; новый Repository/ORM не создавался.
- На Home используется один существующий dynamic `ClayGrid`; второй механизм грида не создавался.
- Повторное использование: `<ClayNavigationMenu Items="items" Options="options" />`, где `items` — `IReadOnlyList<ClayNavigationMenuItem>`.

Эталонные источники: legacy `Inventory/default.aspx(.cs)`, `Inventory/Default/Grid.aspx(.cs)`, `Kesco.Lib.Web.Menu.Inventory.V4/MainMenu.cs`, `Kesco.Lib.Web.Controls.V4/MenuSimple.cs`, `Common/Page.cs`, `Grid/Grid.cs` (`ClearFormParams`), `Kesco.Lib.Entities/SQLQueries.cs`, `Corporate/Queries/Query.cs`; Styles `js/Kesco.Menu.js`, `js/Kesco.V4.js`, `css/Kesco.css`.

## Сравнительная матрица

| Область | Legacy | Clayzor |
|---|---|---|
| DB-разделы | Типы 1/2/3, порядок по `КодЗапроса` | Те же три раздела, один параметризованный SELECT с `ORDER BY ТипЗапроса, КодЗапроса` |
| Обычный запрос | `Grid.aspx?id=N` | `/?id=N`, тот же dynamic grid |
| Внешняя форма | `ФормаСписка` + `idQuery=N` | Сохранено; query/fragment сохраняются, повторный `idQuery` заменяется |
| Активный пункт | Нестабильный `GetHashCode()/menuId` | `КодЗапроса` + текущий `id`, `aria-current=page` |
| Контекстный отчёт | `ПунктМенюОтчета` + `@ФормаОтчета` | Один корневой пункт; без `idQuery`; отсутствующий ключ → disabled с причиной |
| Настройки Inventory | Legacy Windows-группы + `URI_inventory_queries` + `_idQuery=NE` | Те же группы через серверный Windows principal; без подтверждённого URI пункт недоступен |
| Операции | Отдельный корень и legacy `cmd` | По решению владельца корень не переносится; операции находятся в toolbar ClayGrid |
| Очистка параметров | `ClearFormParams` в listener грида | Пункт «Очистить параметры формы» в групповых операциях dynamic grid |
| Help/печать/Excel | Дополнительные кнопки MenuSimple | Используются существующие toolbar ClayGrid, без дублирования |
| Popup | Абсолютное позиционирование, без viewport collision | Fixed popup, viewport bounds, flip, max-height и внутренний `overflow-y:auto` |
| Стартовый `/` | Меню без грида | Меню и нейтральное приглашение; автоматический выбор первого запроса отсутствует |
| `hidemenu`, `return` | Скрытие меню | Поддержано для `1/true` и любого `return` |
| `hidetitle` | Скрытие `.v4pageTitle` | `ShowTitle=false`; команды toolbar не скрываются |
| `return=2` | Увеличение нижнего margin до 95 | Специальный legacy margin не применяется к container-driven layout |

## Реальные данные и конфигурация

Read-only SQL на `DS_user` (SQL Server, база Инвентаризация) показал:

| Тип | Раздел | Количество | Обычный grid | `@KEY` |
|---|---|---:|---:|---:|
| 1 | Инвентаризация | 24 | 19 | 5 |
| 2 | Несоответствия | 42 | 42 | 0 |
| 3 | Отчёты | 12 | 10 | 2 |

Всего 78 пунктов. Контекстный отчёт найден у запроса 3065 (`URI_sim_noservices`). Абсолютных и относительных значений `ФормаСписка` в текущей выборке нет; эти ветки проверены unit-тестами.

Используемые `@`-ключи: `URI_area_search_tree`, `URI_equipment_type`, `URI_inventory_fact`, `URI_inventory_rpt_modifications`, `URI_inventory_rpt_sockets`, `URI_location_search`, `URI_maintenance`, дополнительно `URI_sim_noservices` и `URI_inventory_queries`. Их значения в новом `web.config` отсутствуют, поэтому соответствующие переходы не выдаются за работающие и отображаются disabled с конкретной причиной.

Добавлено приложение `Clayzor.App.Web.Styles` на `http://localhost:5040`, раздающее физический каталог `C:\CLAYZOR-KIMI\Styles` по `/styles/...` без изменения путей в `Запросы`. `URI_styles=http://localhost:5040/styles` явно читается в `Program.cs`. Проверены HTTP 200 и реальные изображения всех 24 пунктов типа 1 (`naturalWidth=16`). Абсолютные и `/styles/...`/bare-имена пиктограмм разрешаются через этот источник; при ошибке изображения показывается fallback без сломанной иконки.

**Блокирующий вопрос для рабочих внешних переходов:** какие фактические URL следует задать для перечисленных ключей и `URI_inventory_queries`? До ответа реализация намеренно не назначает адреса.

## URL-контракты

| `ФормаСписка` | Результат |
|---|---|
| Пусто | `/?id=N` |
| `@KEY` с настроенным URL | URL + `idQuery=N` |
| Абсолютный HTTP(S) | URL + `idQuery=N` |
| Относительный путь | Разрешение от фактического `NavigationManager.Uri`, затем `idQuery=N` |
| Отсутствующий ключ / неизвестная схема | `Href=null`, disabled, причина в `title` и логе |
| `javascript:`, `data:`, `vbscript:` | Заблокировано |

`UriBuilder` и `HttpUtility.ParseQueryString` сохраняют существующие query и `#fragment`. `GetHashCode`, `javascript:` и динамический JS из БД не используются.

## Поведение меню и геометрия

- Клик/tap открывает сразу; pointer hover использует задержку 300 мс.
- Панель позиционируется по `getBoundingClientRect()` активатора и `document.documentElement.clientWidth/clientHeight`.
- Корневая панель открывается вниз/вверх и выравнивается по левому/правому краю активатора; вложенная — вправо/влево и вверх при необходимости.
- `max-height` вычисляется из фактически доступной высоты, scrollbar находится внутри `.clay-navigation-menu__panel`.
- Учитывается масштаб `getBoundingClientRect` относительно `offsetWidth/offsetHeight`, включая zoom 125%/200%.
- Scroll внутри меню не вызывает reposition; страница и window resize пересчитывают геометрию. ResizeObserver удалён: его повторное применение стилей при wheel возвращал scrollTop на один пункт выше низа.
- `Escape`/клик вне закрывают меню; реализованы Tab, Shift+Tab, Enter/Space, стрелки, Home/End, focus-return и `scrollIntoView`.
- Названия пунктов не переносятся: `white-space:nowrap`, `text-overflow:ellipsis`, полный текст в `title`.
- Два экземпляра имеют независимые DOM-id, timers/listeners и состояние; `DisposeAsync` удаляет обработчики и timers.
- Hover/expanded и focus-visible используют золотой inset-shadow без влияния на scroll geometry; нижняя линия меню не дублируется.
- Листовые пункты не вызывают Blazor `OpenAsync` при hover; parent-only timers устраняют лишние renders и дёрганье scroll.
- Пункты имеют min-height 24 px и компактные отступы. Активная зелёная точка — absolute-overlay в свободном padding, без отдельной колонки; offset текста активного/неактивного пункта одинаков (30 px).
- `clayTooltipViewport.js` ограничивает открытые MudTooltip фактическим `clientWidth`; у правой кнопки tooltip правый край=760 при viewport=778, `scrollWidth=clientWidth=778`, мерцания scrollbar нет.

## Home и высота ClayGrid

Удалены AppBar с названием/пользователем, drawer и гамбургер. Переключатель темы через `AdditionalContent` находится внутри корневой полосы меню справа. Глобальные theme/dialog/snackbar providers сохранены. Выбор сохраняется в `localStorage` (`clay-inventory-theme`) и применяется также в `EntityFormLayout` отдельных окон форм.

Home разделена на navigation header, рабочую область грида и существующий pagination/footer грида. Цепочка flex с `min-height:0` и `flex-basis:0` занимает доступную высоту без нового `calc(100vh-N)` для грида. `ClayGridOptions.FillAvailableHeight=true` добавляет container-driven режим; прежние `AutoHeight` и viewport-calc сохранены для других страниц. `ShowTitle` управляет только текстом заголовка. `TitleChanged` передаёт заголовок определения в `PageTitle` вкладки/окна. `CompactLayout` централизованно задаёт padding 5 px, заголовок 14 px/18 px, pagination 28 px и toolbar gap 8 px; Home использует main padding 0/5/5/5 и gap 5 px.

При раскрытии filter/group trays уменьшается рабочая область данных, а pagination остаётся доступна. Popup находится над гридом, диалоги MudBlazor сохраняют более высокий слой.

## Очистка параметров формы

Команда доступна только у инициализированного dynamic grid вне shared-режима. После подтверждения «Вы действительно хотите очистить параметры формы?» сбрасываются:

- фильтры и строка поиска;
- выбор колонок быстрого поиска — к набору определения;
- группировка и сортировка;
- размер страницы;
- порядок и видимость колонок к снимку определения.

Сбрасываются только текущий `GridId`/`CLID`/личный `sharedId=0`. `_dynamicSavedParams` очищается до `SaveDynamicState`, поэтому старые значения не записываются обратно. Повторный `SaveDynamicState` после загрузки видит уже сохранённые дефолты. Общие настройки и другие запросы/пользователи не изменяются. URL forced-фильтры при повторной инициализации того же URL сохраняют свою принудительную семантику.

## Проверки

### Unit и bUnit

- `InventoryMenuTests`: **10/10** — ветки URL, query/fragment, XSS-схемы, отчёт/настройки, реальные базовые URI, состав/порядок/активность, отсутствие «Операций», mapping.
- `ClayNavigationMenuTests`: **7/7** — 80 пунктов, вложенность, два независимых экземпляра, disabled/active, unsafe URL, `AdditionalContent` и активная точка только у выбранного пункта.
- `InventoryHomeTests`: **2/2** — старт без `id`, начальная загрузка и retry после ошибки.
- `ClayGridReinitTests.ClearDynamicState_WritesDefaultsWithoutStaleCacheRestore`: **1/1** — реальный toolbar/menu + подтверждение, дефолты и отсутствие записи старого `99`.
- `ClayGridSectionTests.SectionGrid_FillHeightAndHiddenTitlePreserveToolbar`: **1/1**.

Меню-специфичный прогон: **21/21**. Существующий расширенный профиль: **335/335**. `ClayGridReinitTests`: **15/15**, включая гонку identity, busy-overlay, recoverable retry и маскирование Password/Pwd без добавления отсутствующего пароля. SqlException при init/load обрабатывается в alert с «Повторить», busy-overlay снимается, circuit не разрывается. `dotnet build Clayzor.sln --no-restore`: **0 ошибок**. Полный нефильтрованный testhost не запускался.

### SQL read-only

Проверены реальные counts, поля, порядок, отчёт, пиктограммы, `НастройкиКлиенты`, `НастройкиПараметры` и `INSTEAD OF INSERT`. Изменений `Запросы` и схемы не было, триггеры не отключались (`DisabledTriggers=0`).

### Real browser

- Реальные пункты: 24/42/12; переходы 1001→1012→1001, один grid/меню, корректные заголовки и активность. `PageTitle` меняется Оборудование→Телефонные номера→Оборудование, overlay отсутствует, duplicate `TemplateColumn` key и disposed-ошибки в логах отсутствуют. Фактические размеры: main padding 0/5/5/5, меню 36 px, gap 5 px, заголовок 14 px/18 px, pagination 28 px, theme 34×34 px и right 5 px.
- Typed fixture: 80 длинных пунктов, два меню, вложенность; последний пункт реально открывает 1001.
- 320×568, 360×640, 600×700, 900×700, 1155×750, 1440×900, 900×400: все панели внутри viewport, без page overflow, последний пункт виден после прокрутки.
- CSS zoom 125%/200%: панель внутри viewport; scrollHeight сохраняется.
- Низкий viewport и реальный wheel: после двух wheel-событий `scrollTop=1461`, max=1461, последний пункт полностью виден; обратного скачка нет.
- Проверены hover delay, pointer leave, touch click, outside pointerdown, Escape, стрелки/Home/End, два экземпляра и правый/нижний flip.
- Filter/group: data grid уменьшается, pagination доступна; print dialog и help доступны, навигационный popup закрыт при диалоге.
- Reset: 25→50, после refresh остаётся 50.
- AppBar/drawer отсутствуют; theme toggle меняет «Тёмная тема»→«Светлая тема» и сохраняет выбор; `EntityFormLayout` проверен в dark и light. Длинные названия `nowrap`+ellipsis.
- Ошибка 1025 (`vwSIMКартыСписок`) отображается как alert с «Повторить», без страницы Internal Server Error; последующий переход на 1001 успешен. Текст: `Не удалось загрузить данные запроса SIMКарты #1025.` + `Ошибка: ...` + исходная строка подключения. Текст и кнопка имеют обычный цвет, warning-иконка сохраняет золотой цвет.

## Оставшиеся ограничения

1. Внешние переходы и настройки не проверены на реальных URL до ответа на вопрос о конфигурационных ключах.
2. В `НастройкиПараметры` нет определений `InvRows_1001`, `InvFields_1001`, `InvFilter_1001`, `InvSort_1001`, `InvGroup_1001`, `InvQuickSearch_1001`. Legacy `INSTEAD OF INSERT` игнорирует такие записи, поэтому реальное SQL-сохранение dynamic-настроек 1001 не подтверждено; mock-reset этот пробел не скрывает.
3. Запрос 1004 ссылается на отсутствующий `SUSDB.dbo.vwrpComputerTarget`; его данные не проверены. Это отдельное ограничение определения грида, не меню.
4. Реальные внешние приложения, `Locations` и специальные embedding-размеры не проверялись. Меню автоматически не добавляется в другие формы.

## Изменённые файлы

- Entities: `Inventory/InventoryMenuQuery.cs`, `Inventory/InventoryMenuData.cs`, `SQLQueries.cs`.
- Controls/Navigation: `ClayNavigationMenuItem.cs`, `ClayNavigationMenuOptions.cs`, `ClayNavigationMenu.razor`, `ClayNavigationMenu.razor.cs`, `ClayNavigationMenuPanel.razor`, `ClayNavigationMenuItemContent.razor`.
- Controls/Grid: `ClayGridOptions.cs`, `ClayGrid.razor`, `ClayGrid.razor.cs`, `ClayGrid.Dynamic.cs`.
- Controls assets: `wwwroot/js/clayNavigationMenu.js`, `wwwroot/js/clayTooltipViewport.js`, `wwwroot/css/clay.css`.
- Inventory components: `Components/InventoryMainMenu.razor`, `Components/InventoryMainMenu.razor.cs`, `Components/Pages/Home.razor`, `Components/Pages/Home.razor.cs`, `Components/Pages/MenuTest.razor`, `Components/Layout/MainLayout.razor`, `Components/Layout/EntityFormLayout.razor`, `Components/App.razor`, `Program.cs`.
- Inventory services/config/assets/docs: `Services/InventoryMenuUrlBuilder.cs`, `Services/InventoryMainMenuComposer.cs`, `web.config`, `wwwroot/css/app.css`, `docs/inventory-menu.md`.
- Styles: `src/Clayzor.App.Web.Styles/Clayzor.App.Web.Styles.csproj`, `Program.cs`, `appsettings.json`; проект добавлен в `Clayzor.sln`.
- Tests: `InventoryMenuTests.cs`, `ClayNavigationMenuTests.cs`, `InventoryHomeTests.cs`, `ClayGridReinitTests.cs`, `ClayGridSectionTests.cs`.

Работа ведётся в `main`; последующие commit/push/merge не выполняются без отдельной команды пользователя.
