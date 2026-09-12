SPT-FOV-Fix — порт на SPT 4.1.3
=================================

Источник: https://github.com/space-commits/SPT-FOV-Fix (v4.0.1, GUID com.fontaine.fovfix)

ИСПРАВЛЕННЫЙ БАГ (найден после деплоя, реальный тест в игре): присед ломал FOV на ADS
-----------------------------------------------------------------------------------------
Симптом (со слов пользователя): прицелился — всё ок; присел/встал хоть раз — зум
ADS сбивается и остаётся сломанным, пока не прицелиться заново (снять прицел/навести
заново чинит).

Причина — найдена точно по стек-трейсу в дебаг-логе (см. ниже): ВАНИЛЬНЫЙ метод
`ProceduralWeaponAnimation.OnAimOrPoseChanged` сам вызывает `CameraManager.SetFov(x, ...)`
при КАЖДОЙ смене Pose (присед/встал), не только при начале/конце ADS. Для оптики он
жёстко ставит `x = 35f` (захардкожено в самой игре, наши множители зума полностью
игнорирует). Цепочка вызовов из лога:
  Player.TogglePose() [клавиша C] -> MovementState.ChangePose -> MovementContext.SetPoseLevel
  -> Player.CG_InitHandsContainer -> ProceduralWeaponAnimation.set_Pose
  -> OnAimOrPoseChanged(forced: true) -> CameraManager.SetFov(35f, 1f, applyFovOnCamera: False)

Наш `FovController.ChangeMainCamFOV()` пересчитывает зум только когда реально меняются
отслеживаемые значения (ADSWatcher/ScopeFOVWatcher/ToggleZoomWatcher/OpticWatcher) — при
смене позы во время уже активного ADS ничего из этого не меняется, поэтому вызов не
перезапускается, и вражеский сброс на 35° зависает до следующего реального ADS-тоггла.

Фикс (FovPatches.cs, `OnAimOrPoseChangedFixPatch`): Harmony-постфикс на тот же
`OnAimOrPoseChanged`, который сразу после вызывает `Plugin.FovController.ChangeMainCamFOV()`,
перезаписывая то, что ванильный код только выставил. Минимальный диф — не трогает
остальную (полезную) логику `OnAimOrPoseChanged` (tactical reload, sway, breath и т.д.).

Для диагностики (до нахождения причины) также добавлены debug-логи, гейтящиеся новым
конфигом "Enable Debug Logging" (`..0. Debug`, default true) — `Utils.DLog`/`DLogThrottled`
по всему моду плюс прямые хуки на `CameraManager.SetFov` и на сеттер `CameraManager.Fov`
(с stack trace вызывающего) — это и раскрыло точную причину. Логи можно оставить включёнными
или выключить в конфиге, они больше не нужны для функционала, только для диагностики.

Сборка: netstandard2.1 + прямые HintPath-ссылки на DLL живого клиента
D:\Games\SPT-4.1_test (тот же шаблон, что AkShaderFix/DoorDash), вместо нугет-пакетов
оригинала (BepInEx.Core/PluginInfoProps, UnityEngine.Modules — недоступны в этом
окружении). 0 ошибок, 0 предупреждений.

Главная сложность: FovPatches.cs (ядро мода — сдвиг камеры при ADS и сам FOV) был
завязан на обфусцированные имена из сборки клиента ИЗ ОРИГИНАЛЬНОЙ 4.0-версии.
В 4.1.3 обфускация другая (SPT/BSG каждый раз пересобирает её заново), так что
все такие имена разобраны decompile'ом живой Assembly-CSharp.dll по отдельности:

1. LerpCameraPatch (сдвиг камеры при ADS — ГЛАВНАЯ фича мода) — НЕ потребовал
   изменений вообще. Все приватные поля ProceduralWeaponAnimation (____aimSwayBlender,
   ____headRotationVec, ____vCameraTarget, ____tacticalReload, ____cameraIdenity,
   ____rotationOffset и т.д.) совпали 1:1 по именам в живой 4.1.3 сборке — подтверждено
   прямым decompile. Единственная правка: убран один хвостовой вызов
   `__instance.method_19(dt)` — обфусцированный метод без однозначного 4.1.3-эквивалента
   (несколько кандидатов вида `void Xxx(float dt)`, не угадано наугад) — сама позиция
   камеры (суть патча) это не затрагивает.

2. PwaWeaponParamsPatch (триггер "оружие сменилось") — таргет `method_23` заменён на
   `InitWeaponData(...)`, подтверждённый decompile: именно он первым делом выставляет
   `_firearmController` (`_firearmController = firearms as Player.FirearmController;`),
   что и предполагает логика патча.

3. FovRangePatch/FovValuePatch (расширенный диапазон FOV в настройках):
   - `GClass1085` → `EFT.Settings.Game.GameSettingsGroup` (открытый класс, не обфусцирован).
   - `Class1841.method_0` → `GameSettingsGroup+CG_Ctor.method_0` — тот же clamp-лямбда
     (`Mathf.Clamp(x, 50, 75)`), просто лежит как nested compiler-generated класс
     внутри GameSettingsGroup в 4.1.3, а не отдельным top-level классом.

4. CameraClass → EFT.CameraControl.CameraManager (открытый класс с собственным
   статическим `.Instance`, не через `Singleton<T>`). `SharedGameSettingsClass` →
   `EFT.Settings.SettingsManager` (тот же доступ `.Game.Settings.*`/`.Control.Settings.*`,
   подтверждён по образцу из живого кода CameraManager). `ProceduralWeaponAnimation.Single_2`
   (базовый FOV) → `.HeadBobbing` (переименовано обманчиво, но по факту читает то же самое
   `Settings.Game.FieldOfView.Value`, подтверждено по использованию в самом движке).
   `Boolean_0` (флаг левого плеча) → публичное свойство `.LeftStance`.

5. Выкинуто (не угадывалось наугад):
   - `CloneItemPatch` (`GClass3380.CloneItem`) — нишевая фича (сохранение офсета камеры
     при тестировании оружия в хидауте), сам автор мода не был уверен в её назначении.
   - `CameraUpdatePatch`, `OpticPanelPatch`, `ScopeZoomPatch` — в самом оригинале
     НИКОГДА не включались в `Plugin.Awake()` (мёртвый код), удалены как есть.
   - `Utils.IsSight(Mod)` — использовал `TemplateIdToObjectMappingsClass.TypeTable`,
     нигде в моде не вызывался (тоже мёртвый код).

Побочное изменение в RealismModPort\port\Plugin.cs и PluginConfig.cs
-----------------------------------------------------------------------
Для совместимости (RealismCompat.cs читает RealismMod.Plugin.ServerConfig и
RealismMod.PluginConfig напрямую) классы Plugin/ServerConfig/PluginConfig в нашем
RealismMod-порте были internal → сделаны public (чисто видимость сборки, функционал
не менялся). Также исправлена проверка автоопределения в FOV Fix: оригинал искал
GUID "RealismMod" в Chainloader.PluginInfos, наш порт зарегистрирован как
"com.realismmod.ballistics.port" — проверяются оба GUID. RealismMod-порт пересобран
(0 ошибок) после этой правки, см. RealismModPort\port\bin\Debug\netstandard2.1\.

Деплой
------
В игру не устанавливался, только собран и упакован в архив.
