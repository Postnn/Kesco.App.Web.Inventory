# Tel: соответствие legacy и проверки Tel_2

База аудита: `promts/Tel_2.md`, legacy `Inventory/Telefon/Tel.aspx(.cs)` и `Kesco.Lib.Entities/Corporate/Phones/Phone.cs`. Реализация — `Components/Pages/Telefon/Tel.razor(.cs)` и `Services/Phone*.cs`.

## Матрица полей и зависимостей

| Элемент | Legacy | Clayzor / пересчёты | Проверка |
|---|---|---|---|
| Номер телефона | Строка до 4, цифры/#/*; при смене очищает parallel после подтверждения | `OnNumberChanged`, `RecalculateFullNumber`; серверная проверка дублей | `PhoneFormValidationTests`, browser |
| Полный номер | DisplayedFrom → DrivenBy → префикс станции | Клиентский preview; перед Save повторно вычисляется SQL из актуальных записей | SQL `SELECT_ТелефонАвторитетныйПолныйНомер` |
| Станция | Очищает PBX после подтверждения; обновляет порт, ATS, тип, заказчика | `OnStationChangedAsync`, версии capability/select, фильтры групп/оборудования/розеток | async tests, browser |
| Тип номера | Фильтрует оборудование; radio 0/100/101 принудительно | `OnTypeChangedAsync`, capability, nullable radio, фильтры | `NewChoices_DistinguishNullFromZero` |
| Порт АТС | Свободные + собственный текущий | Тот же набор; серверная повторная проверка | SQL `SELECT_ТелефонПортДоступен` |
| Переадресация | Необязательный +, цифры/#/*, max50 | `PhoneFormValidator`; предупреждение о чужом сотруднике не блокирует Save | unit + browser |
| Группа перехвата | 0…15 для 1…99; именованная секция для CCM | `Program.cs` переносит значения `pickupGroups` из web.config; пустая конфигурация fail closed | `PickupGroup_MissingConfigurationFailsClosed` |
| Номер автонабора | Доступен по типу/станции | `GetCapabilitiesAsync`, `UpdateDependentState` | `PhoneFormRulesTests` |
| Параллельные вызовы | После автонабора, быстрый ввод и диалог | `ClayGrid`, quick input/Enter, диалог с отдельным черновиком | `ParallelCallDialogValidation_*`, browser |
| Сотрудник | Подставляет `Employee.OrganizationId`, ATS и default phone | `Сотрудники.КодЛицаЗаказчика`; версии selection/ручных правок после каждого await | `TelAsyncStateTests` |
| Ведомый от | Полный номер и допустимость связи | `OnDrivenByChanged`, server cycle/missing/station validation | unit + SQL |
| Отображаемый от | Приоритет полного номера | `OnDisplayedFromChanged`, server validation | unit + SQL |
| Компания заказчик | Обновляет связанные проверки | `OnCustomerChanged`; ручной ввод защищён от позднего ответа сотрудника | `Employee_ManualCustomerChangeWins` |
| Розетка | Office/Alive/Free + расположение оборудования | `OnSocketChanged`; fail closed при оборудовании без расположения | `Socket_DraftEquipmentWithoutLocationFailsClosed` |
| Подключено | Влияет на обязательность ATS/сотрудника | `OnConnectedChanged`, серверная валидация | `PhoneFormValidationTests` |
| Описание АТС | Автозаполнение без перезаписи ручного ввода | `OnAtsDescriptionChanged`, `_atsVersion`, `_atsEditVersion` | `AtsDescription_ManualEditWinsOverLateResponse` |
| Основной номер сотрудника | Пустой/NotDisplayed → Displayed | `ResolveDirectoryAfterDefault`; другие режимы сохраняются | `DefaultPhone_UpdatesOnlyEmptyOrHiddenDirectory` |
| Входит в группы | Ссылки на группы | `PhoneGroupMembership`, `clayOpen` для форм | browser |
| Члены группы | Сохраняются до основной записи у существующего номера | `PhoneSaveBuilder`: group → main → parallel → equipment; INSERT: main → group | `Build_UsesLegacyGroupAndMainWriteOrder` |
| Оборудование | Физический PK `(PhoneId, EquipmentId)`; подключение влияет на связь | Дубль EquipmentId запрещён; смена подключения — точные DELETE+INSERT; SQL WHERE включает ConnectionId | `Build_ConnectionChangeUsesPreciseDeleteAndInsert` |
| Срок возврата | Не в прошлом; checkbox включает дату | `OnHasReturnDateChanged`, UTC business date | `ReturnDate_UsesUtcBusinessDateBoundary` |
| Услуги связи | Новый обычный тип — выбор обязателен; 0/100/101 принудительно | `int?`, нет неявного 0; A→B→A сохраняет пользовательский выбор | `NewChoices_DistinguishNullFromZero` |
| Отображение в справочнике | Новая PBX — NotDisplayed; остальные без имитации выбора | `int?`, `ResolveDirectory`; parallel требует отображения | unit + browser |
| Примечания | max300 | `PhoneFormValidator` | unit + CRUD 1002 |
| Changed | Локальное время браузера на дату события | `ClayChanged`, в гридах `ClayLocalTime` | `LocalTime_UsesOffsetAtRecordedUtcDate` |

## Статус TEL-01…TEL-22

| ID | Статус реализации | Доказательство / ограничение |
|---|---|---|
| TEL-01 | Исправлено с уточнением по фактической схеме | В БД подтверждён PK только `(КодТелефонногоНомера, КодОборудования)`. Две связи одного EquipmentId запрещены; составной ключ используется для точной замены/обновления/удаления. `OtherPhones` группируется и не падает на повторных строках. |
| TEL-02 | Исправлено | Порядок соответствует `Phone.Save`; общая транзакция и `@@IDENTITY` сохранены. Определение базового триггера недоступно (`OBJECT_DEFINITION` вернул NULL), поэтому влияние триггера не выдумывается. |
| TEL-03 | Исправлено в планировщике | Diff PBX и parallel (включая `NotUsed` и задержки) добавлен; metadata-only equipment игнорируется. Внешние вызовы не выполнялись. |
| TEL-04 | Исправлено | Prefix-проверка выполняется и для зависимых; exact duplicate только для независимых. Полный номер пересчитывается сервером. |
| TEL-05 | Исправлено | Версии employee/customer/ATS + identity checks и Dispose; обратное завершение TCS покрыто `TelAsyncStateTests`. |
| TEL-06 | Исправлено | SQL читает `Сотрудники.КодЛицаЗаказчика`, как legacy `Employee.OrganizationId`. |
| TEL-07 | Исправлено | `OnSocketChanged` обновляет EquipmentSource и версию select. |
| TEL-08 | Исправлено | Nullable UI отличает отсутствие выбора от DB 0; принудительные режимы и копирование сохранены. |
| TEL-09 | Исправлено | Пустой/0 → 1, остальные значения не изменяются. |
| TEL-10 | Исправлено на уровне серверной валидации | Перед Save проверяются станция членов, допустимость оборудования по составному ключу и розетка/расположение; существующие связи сохраняются. Атомарность конкурентного занятия порта/розетки зависит от ограничений БД и требует отдельного concurrency-теста. |
| TEL-11 | Частично, архитектурная зависимость | В `Tel.aspx.cs` ограничение по компании заказчика не найдено; misleading UI-текст удалён. Сохранена проверка роли 43. Общий `Employee CurrentUser` в решении отсутствует; второй механизм не создавался. |
| TEL-12 | Исправлено в текущем circuit | `_saving` устанавливается до retry; хранятся только failed operations; SQL Save не повторяется. После delete успешный retry закрывает форму. Журнала pending между circuits нет — при навигации выдаётся предупреждение. |
| TEL-13 | Исправлено | Если выбранный заказчик присутствует среди допустимых, таблица результата очищается полностью. |
| TEL-14 | Исправлено | `ShowIssues` вызывается и после успешного Save, warning/info переживают refresh. |
| TEL-15 | Исправлено | Секция расположена сразу после автонабора; добавлены quick input/Enter и `ClayGrid`. |
| TEL-16 | Исправлено | Формат, max50, self/duplicate, задержки 0…255; legacy `Math.Round(..., ToEven)`; диалог остаётся открытым. |
| TEL-17 | Исправлено | Общий `ClayLocalTime` использует offset на дату события, лето/зима покрыты bUnit. |
| TEL-18 | Исправлено | Фактический web.config содержит именованные группы в плоском значении, читаемом `Program.cs`; произвольная группа при пустом списке запрещена; отсутствие LocationId fail closed. |
| TEL-19 | Исправлено | Обработчики Customer/Socket/Employee/Station/Type/Driven/Displayed обновляют модель, filters и full number без повторных запросов. Матрица приведена выше. |
| TEL-20 | Исправлено | SQL error service учитывается отдельно от not found; добавлены managed error и «Повторить», late-response guards сохранены. |
| TEL-21 | Частично | Подтверждённые enum добавлены в `Clayzor.Lib.Entities/DatabaseEnums.cs`; роли/станции/типы используются в новой логике. Полный переход остальных форм и `CurrentUser` не входит в Tel-only правку. |
| TEL-22 | Выполнено в доступном окружении | 294 профильных теста; `dotnet build Clayzor.sln` успешен. В браузере два Save записи 1002 с чтением результата SQL, new/copy, 1155/900/600 px. Полный testhost завис без запуска тестов и остановлен. Внешние АТС и CRUD связей не подтверждены из-за пустых таблиц. |

## Фактические проверки

- `dotnet build Clayzor.sln --no-restore` — успешно, 0 ошибок; существующие NuGet/compiler warnings сохраняются.
- Профильный `dotnet test` — 294/294, включая `TelAsyncStateTests`, `ClayLocalTime`, составные ключи, radio, pickup и select filters.
- В браузере запись 1002 сохранена с `CLAY_TEST_TEL2`, результат прочитан SQL; повторный Save восстановил `CLAY_TEST`. Внешних операций синхронизации у этой записи нет.
- Новая форма не имеет выбранных radio. Существующая и копия сохраняют значения; строковые поля не содержат Razor-литералов.
- 1155/900/600 px: `scrollWidth == innerWidth`, одно-/двухколоночная компоновка работает. Временные скриншоты не включены в исходники.
- Полный `dotnet test` завис после инициализации testhost, не выполнив тесты; процесс остановлен, поэтому успешным не считается.

## Ограничения проверки

- Реальные команды Meridian/Xorcom/CCM не запускались; корректность их внешнего эффекта и повторный pending после переподключения circuit не подтверждены.
- Сохранение связей PBX и оборудования с подходящими фикстурами требует отдельных тестовых записей; физический PK подтверждён read-only запросом.
- Общая авторизационная модель `Employee CurrentUser` отсутствует в текущем baseline и не дублируется в Tel.


## Исправления Tel_3

| ID | Статус | Что изменено и чем подтверждено |
|---|---|---|
| P1-01 | Исправлено в SQL; реальные фикстуры отсутствуют | `SELECT_ТелефонРозеткаДопустимаВОфисе` использует те же интервалы `L/R` для дерева офиса, что и поиск. Собственная розетка освобождается только от проверки занятости; `Работает` и расположение проверяются всегда. Аналогичное правило применено к расположениям чернового оборудования. `SocketValidation_UsesOfficeSubtreeAndChecksContextForCurrentSocket` фиксирует структуру SQL. Оба запроса выполнены read-only на SQL Server с контрольными ID и вернули 0 без ошибок. В тестовой БД нет станций с расположением и розеток, поэтому сценарии «то же помещение / другой кабинет / другой офис / собственная занятая» на реальных записях не проверены; справочники не сидировались. |
| P1-02 | Исправлено | `PhoneSyncPlanner` включает Meridian/Xorcom при структурном equipment diff, а не только при main diff. `Sync_StructuralEquipmentDiffIncludesStation` покрывает добавление, удаление и замену для CCM, Meridian и Xorcom; metadata-only тест запрещает лишнюю синхронизацию. |
| P1-03 | Контракт подтверждён; маршрутизация исправлена | `OBJECT_DEFINITION(sp_Xorcom_PhoneNumberSync)` подтвердил чтение текущей станции и связей по `PhoneId`; отдельного имени станции процедура не принимает. При A→B старая АТС очищается до Save, новая синхронизируется после Save. При Delete Xorcom вызывается до физического удаления. Ошибка SQL после предварительной очистки запускает компенсационное восстановление старой АТС. `TelAsyncStateTests` проверяют порядок вызовов, запрет Save при ошибке pre-sync и компенсацию Save/Delete. Реальные команды АТС не запускались; это mock ATS, а не подтверждение внешнего эффекта. Компенсация не является распределённой транзакцией; её ошибка выводится отдельным предупреждением. |
| P2-04 | Исправлено | `PhoneSocketSelectDataSource` возвращает пустой результат без SQL при `StationId=null` независимо от расположений оборудования. `Socket_MissingStationFailsClosedWithEquipmentLocations`. Восстановление уже выбранного значения через `GetByIdAsync` не изменено. |
| P2-05 | Исправлено | Save повторно получает capabilities для фактических StationId/PhoneTypeId непосредственно перед нормализацией и серверной записью. `Save_RefreshesCapabilitiesBeforeNormalization` задерживает старый ответ и проверяет, что в `PhoneSaveRequest` попали только свежие порты и матрица. |
| P2-06 | Операционное ограничение задокументировано | Durable pending/outbox не добавлен. В `phone-form.md` записан регламент: до успешного повтора не закрывать форму и не обновлять страницу; после потери circuit использовать legacy-форму или регламентную синхронизацию администратора. Автоматическое восстановление между circuits не заявляется. |
| P3-07 | Сознательно оставлено | Пустые секции сохраняют краткое пояснение: после ревью пользователь отдельно потребовал уменьшить его высоту, а не убрать текст. Общая высота empty-строки ClayGrid установлена 32 px, как стандартный заголовок. |

Проверка Tel-related набора после исправлений: **112/112** (`Phone*` + `TelAsyncStateTests`). Объединённый профильный прогон: **305/305**. `dotnet build Clayzor.sln --no-restore` — 0 ошибок. Сюда входят SQL-структура, select fail-closed, planner и mock ATS. Это не заменяет реальные SQL-фикстуры и внешние АТС.

В браузере запись 1002 сохранена с примечанием `CLAY_TEST_TEL3`, результат прочитан непосредственно из SQL; затем выполнен второй Save и восстановлено `CLAY_TEST`. Ошибок формы нет, empty-строка 32 px, горизонтального переполнения нет. Внешние АТС у этой записи не вызывались. `DisabledTriggers = 0`.


## Исправления Tel_4

| ID | Статус | Что изменено и чем подтверждено |
|---|---|---|
| R1 | Исправлено | `PhoneSyncPlanner.BuildDelete` использует только `OriginalPhone`, `OriginalRelations` и сохранённую станцию. Черновые StationId, порт и EquipmentId не попадают в план Delete. `Sync_DeleteUsesPersistedStationAndEquipmentOnly` проверяет отсутствие draft-CCM/порта; bUnit `Xorcom_DeleteRunsBeforeSqlDelete` покрывает сценарий open A → draft B → Delete без Apply. |
| R2 | Исправлено | Перед Xorcom pre-sync выполняются локальная и read-only серверная бизнес-валидация. Финальная проверка в `PhoneFormService.SaveAsync` сохранена; дополнительная C#-проверка ролей не добавлялась. `Xorcom_BusinessValidationBlocksPreSync` подтверждает отсутствие внешнего вызова и SQL Save при занятом порте. |
| R3 | Исправлено | Авторитетный полный номер читается через fail-closed scalar API: connectivity SQL error выбрасывается, `null` также блокирует Save. Fallback к короткому номеру удалён. `Save_FullNumberConnectivityErrorFailsClosed` подтверждает исключение и отсутствие INSERT/UPDATE. |
| R4 | Проверено в браузере | Tel 1002, EquipmentForm 7, EquipmentIns, ModelEquipmentForm 2270 проверены на 600/900/1155 px: 12/12 smoke. Toolbar совпадает с границами формы, золотая линия 2 px, footer виден, прокрутка работает и восстанавливается, кнопки доступны через `elementFromPoint`, горизонтального переполнения нет. |
| R5 | Приёмочный риск сохраняется | Реальных фикстур розеток и расположений в тестовой БД по-прежнему нет; корректность выборки по соседним кабинетам на реальных записях не подтверждена. Не утверждается внешний эффект Xorcom, восстановление circuit или durable outbox. Реальные АТС не запускались. |

Объединённый профильный прогон: **308/308**, `dotnet build Clayzor.sln --no-restore` — 0 ошибок. Реальный Save записи 1002 с `CLAY_TEST_TEL4` прочитан из SQL, затем вторым Save восстановлено `CLAY_TEST`; ошибок формы нет, `DisabledTriggers = 0`.


## Исправления Tel_5

| ID | Статус | Что изменено и чем подтверждено |
|---|---|---|
| TEL4-F01 | Исправлено | Предварительный план используется только для одноразового Xorcom pre-cleanup. После `Service.SaveAsync` итоговый план пересобирается с мутированным сервером `request.Phone.FullNumber` и исключает уже выполненные pre-операции. `Save_ServerFullNumberChangeTriggersPostSync` покрывает Xorcom и Meridian; `Save_UnchangedServerFullNumberDoesNotSync` запрещает лишний вызов. A→B проверяет ровно одну очистку старой станции. Повторный SQL Save не выполняется. |
| TEL4-F02 | Исправлено с остаточной гонкой | `GetDeleteSnapshotAsync` непосредственно перед Delete перечитывает телефон, станцию и связи через read-only SQL. `BuildDelete` получает только этот persisted snapshot. `Delete_UsesFreshPersistedEquipment` проверяет новые EquipmentId и игнорирование черновика; `Delete_MissingPersistedPhoneDoesNotSync` запрещает АТС и повторный Delete при уже удалённой записи. Гонка между snapshot и физическим DELETE не устранена и не выдаётся за распределённую транзакцию. |
| TEL4-F03 | Исправлено | Cancel, Refresh, внутренняя навигация и Delete используют общее `CloseConfirmation`: при pending явно сказано, что SQL данные сохранены, повтор из формы будет потерян и потребуется ручная синхронизация. `PendingSync_CloseConfirmationExplainsSavedDataAndManualSync` и `Draft_CloseConfirmationKeepsOriginalMessage` разделяют оба состояния. |
| TEL4-F04 | Ограничение сохраняется | Реальные CRUD связей, конкурентное занятие портов/розеток, внешний эффект Xorcom/Meridian/CCM и durable outbox не подтверждены. Внешние АТС не запускались. |

Tel-related проверка: **122/122**. Объединённый профильный прогон: **315/315**. `dotnet build Clayzor.sln --no-restore` — 0 ошибок. Полный `dotnet test` без фильтра повторно завис сразу после инициализации testhost, за 180 секунд тесты не запустились; процесс остановлен и полный набор не считается пройденным.

В браузере запись 1002 сохранена с `CLAY_TEST_TEL5`, значение прочитано из SQL; вторым Save восстановлено `CLAY_TEST`. Ошибок формы и горизонтального переполнения нет, `DisabledTriggers = 0`. Реальные сценарии отделены от mock; внешние АТС не вызывались.

## Исправления Tel_6

| ID | Статус | Что изменено и чем подтверждено |
|---|---|---|
| TEL5-UI-01 | Исправлено | `HasUnsavedDraft` и `HasPendingSync` вычисляются раздельно. Cancel и внутренняя навигация при наличии обоих состояний показывают оба предупреждения, при одном pending — только предупреждение об АТС. Delete всегда явно спрашивает об удалении номера и его связей и добавляет pending-предупреждение при необходимости. Refresh запрашивает подтверждение только при наличии нового черновика и сохраняет pending в текущем circuit. bUnit вызывает реальные обработчики и MudBlazor-диалоги для всех комбинаций draft/pending; отказ не изменяет модель, pending и не вызывает SQL. |

Проверки: `TelAsyncStateTests` — **31/31**, Tel-related — **136/136**, объединённый профильный прогон — **329/329**, `dotnet build Clayzor.sln --no-restore` — 0 ошибок. Реальные команды АТС не вызывались; ограничения интеграционных испытаний из Tel_5 сохраняются.

## Исправления Tel_7

| ID | Статус | Что изменено и чем подтверждено |
|---|---|---|
| TEL6-UI-01 | Исправлено | Из `Disabled` toolbar Delete удалено только условие pending; защиты `_loading`, `_saving`, `!_canEdit` и явное подтверждение сохранены. bUnit нажимает rendered `MudButton` для всех комбинаций draft/pending, проверяет «Нет» без SQL/АТС и «Да» с persisted snapshot. Внутренняя навигация проверена через `NavigationManager` и `NavigationLock` для тех же комбинаций, включая отказ без смены URL. |

Проверки: `TelAsyncStateTests` — **36/36**, Tel-related — **141/141**, объединённый профильный прогон — **334/334**, `dotnet build Clayzor.sln --no-restore` — 0 ошибок. `NavigationLock.ConfirmExternalNavigation` использует системный `beforeunload`-диалог, текст которого браузер не позволяет настраивать. Реальные команды АТС не вызывались; прежние интеграционные ограничения сохраняются.

В браузере на записи 1002 проверены доступная toolbar Delete, явный вопрос и отказ, а также `NavigationLock` при реальном внутреннем переходе: отказ сохранил URL и черновик, подтверждённый Refresh восстановил `CLAY_TEST`. Pending без mock в браузере не создавался, чтобы не вызывать внешние АТС; его UI-сценарии покрыты bUnit. SQL-запись не изменялась, `DisabledTriggers = 0`.
