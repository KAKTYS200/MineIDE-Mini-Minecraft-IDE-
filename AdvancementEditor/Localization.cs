using System;

namespace MineIDE.AdvancementEditor;

/// <summary>
/// Lightweight RU/EN localization for the advancements editor. The app UI is
/// Russian-first; a static switch lets a future settings option flip the strings.
/// </summary>
public static class Loc
{
    private static bool _en;

    /// <summary>When true, English strings are returned; default is Russian.</summary>
    public static bool English
    {
        get => _en;
        set
        {
            if (_en == value) return;
            _en = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? Changed;

    private static string T(string ru, string en) => _en ? en : ru;

    // ---- editor strings ----
    public static string EditorTitle => T("Конструктор достижений", "Advancement Builder");
    public static string TriggerLabel => T("Тип триггера:", "Trigger:");
    public static string IdLabel => T("ID достижения", "Advancement ID");
    public static string TitleLabel => T("Название", "Title");
    public static string DescriptionLabel => T("Описание", "Description");
    public static string IconLabel => T("Иконка (предмет)", "Icon (item)");
    public static string ParentLabel => T("Родительское достижение", "Parent advancement");
    public static string NoParent => T("(нет — корень)", "(none — root)");
    public static string FrameLabel => T("Рамка", "Frame");
    public static string ShowToast => T("Показывать тост", "Show toast");
    public static string AnnounceChat => T("Объявлять в чат", "Announce to chat");
    public static string Hidden => T("Скрытое", "Hidden");
    public static string TriggerItemLabel => T("Предмет (для триггера)", "Item (for trigger)");
    public static string TriggerEntityLabel => T("Моб (entity id)", "Entity id");
    public static string TriggerLevelLabel => T("Уровень", "Level");
    public static string TriggerLootLabel => T("Loot table", "Loot table");
    public static string RewardLabel => T("Награда", "Reward");
    public static string RewardExp => T("Опыт", "Experience");
    public static string RewardItem => T("Предмет-награда", "Reward item");
    public static string RewardCount => T("Кол-во", "Count");
    public static string GridHint => T("Сетка 5×5 — дерево достижений. Перетащите предмет из списка на пустую ячейку, чтобы создать достижение, или кликните по ячейке. Линии соединяют родителя с детьми.", "5×5 grid — the advancement tree. Drag an item from the list onto an empty cell to create an advancement, or click a cell. Lines connect parents to children.");
    public static string NewBtn => T("Новое", "New");
    public static string SaveBtn => T("Сохранить", "Save");
    public static string DeleteBtn => T("Удалить", "Delete");
    public static string ItemsLabel => T("Предметы", "Items");
    public static string JsonLabel => T("JSON (обновляется автоматически)", "JSON (updates automatically)");
    public static string CopyJson => T("Скопировать JSON", "Copy JSON");
    public static string ErrorsTitle => T("Ошибки:", "Errors:");
    public static string NoModel => T("Кликните по пустой ячейке сетки или перетащите предмет, чтобы создать достижение.", "Click an empty grid cell or drag an item onto it to create an advancement.");
    public static string ModelProperties => T("Свойства достижения", "Advancement properties");
    public static string StatusSaved => T("Сохранено", "Saved");
    public static string StatusNew => T("Новое достижение", "New advancement");
    public static string StatusLoaded => T("Загружено", "Loaded");
    public static string BackgroundLabel => T("Фон", "Background");
    public static string CreateAdvancement => T("Создать достижение", "Create advancement");
    public static string CreateLinked => T("Создать связанное достижение", "Create linked advancement");
    public static string CanvasHint => T("Правый клик на пустом месте — создать достижение; перетащите узел мышью; правый клик по узлу — создать связанное (с линией-связью).", "Right-click empty space to create an advancement; drag nodes with the mouse; right-click a node to create a linked one (connected by a line).");
    public static string BackgroundHint => T("Фон холста (как в игре): gui/advancements/backgrounds", "Canvas background (like in-game): gui/advancements/backgrounds");
    public static string GreenBackground => T("Зелёный (трава)", "Green (grass)");

    // ---- trigger names ----
    public static string TriggerInventory => T("Получить предмет (inventory_changed)", "Obtain item (inventory_changed)");
    public static string TriggerKill => T("Убить моба (player_killed_entity)", "Kill entity (player_killed_entity)");
    public static string TriggerLevel => T("Достичь уровня (player_level)", "Reach level (player_level)");
    public static string TriggerLoot => T("Открыть сундук (player_generates_container_loot)", "Open chest (player_generates_container_loot)");
    public static string TriggerBrew => T("Сварить зелье (brewed_potion)", "Brew potion (brewed_potion)");
    public static string TriggerBeacon => T("Построить маяк (construct_beacon)", "Construct beacon (construct_beacon)");
    public static string TriggerConsume => T("Съесть предмет (consume_item)", "Consume item (consume_item)");
    public static string TriggerTotem => T("Использовать тотем (used_totem)", "Use totem (used_totem)");
    public static string TriggerTick => T("Прожить тик (tick)", "Survive a tick (tick)");
    public static string TriggerImpossible => T("Невозможно (impossible)", "Impossible");

    // ---- frames ----
    public static string FrameTask => T("Задача (task)", "Task");
    public static string FrameGoal => T("Цель (goal)", "Goal");
    public static string FrameChallenge => T("Испытание (challenge)", "Challenge");

    public static string TriggerName(Models.AdvancementTrigger t) => t switch
    {
        Models.AdvancementTrigger.InventoryChanged => TriggerInventory,
        Models.AdvancementTrigger.PlayerKilledEntity => TriggerKill,
        Models.AdvancementTrigger.PlayerLevel => TriggerLevel,
        Models.AdvancementTrigger.PlayerGeneratesContainerLoot => TriggerLoot,
        Models.AdvancementTrigger.BrewedPotion => TriggerBrew,
        Models.AdvancementTrigger.ConstructBeacon => TriggerBeacon,
        Models.AdvancementTrigger.ConsumeItem => TriggerConsume,
        Models.AdvancementTrigger.UsedTotem => TriggerTotem,
        Models.AdvancementTrigger.Tick => TriggerTick,
        Models.AdvancementTrigger.Impossible => TriggerImpossible,
        _ => TriggerImpossible
    };

    public static string FrameName(Models.AdvancementFrame f) => f switch
    {
        Models.AdvancementFrame.Task => FrameTask,
        Models.AdvancementFrame.Goal => FrameGoal,
        Models.AdvancementFrame.Challenge => FrameChallenge,
        _ => FrameTask
    };
}
