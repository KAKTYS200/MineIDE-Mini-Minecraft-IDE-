using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineIDE.AdvancementEditor.Models;

namespace MineIDE.AdvancementEditor.Services;

/// <summary>
/// Builds a valid Minecraft 1.20.1 advancement JSON from <see cref="AdvancementDocument"/>
/// and validates the whole tree. UI-free — reusable by other editors.
/// </summary>
public static class AdvancementJsonGenerator
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Namespace prepended to ids in generated JSON (e.g. the mod id).</summary>
    public static string Namespace { get; set; } = "mine_ide";

    /// <summary>Background texture used for the root node (gui/advancements/backgrounds/&lt;name&gt;).</summary>
    public static string BackgroundName { get; set; } = "adventure";

    /// <summary>
    /// Generates the advancement file JSON for the whole tree as it will be saved:
    /// one file per node (Minecraft stores each advancement as its own JSON file).
    /// When the document holds several nodes the result is a JSON object mapping
    /// each node's id to its node JSON — handy for the live preview; files written
    /// to disk use <see cref="GenerateNode"/> per node.
    /// </summary>
    public static string Generate(AdvancementDocument doc)
    {
        var models = doc.Models;
        if (models.Count == 0)
            return new JsonObject().ToJsonString(Indented);
        if (models.Count == 1)
            return GenerateNode(models[0]);

        var root = new JsonObject();
        foreach (var model in models)
            root[FullId(model.Id)] = BuildNode(model);
        return root.ToJsonString(Indented);
    }

    /// <summary>Generates one advancement file (the node itself, no id wrapper).</summary>
    public static string GenerateNode(AdvancementModel m)
    {
        if (m == null) return new JsonObject().ToJsonString(Indented);
        return BuildNode(m).ToJsonString(Indented);
    }

    private static JsonObject BuildNode(AdvancementModel m)
    {
        var node = new JsonObject();

        // ---- display ----
        var display = new JsonObject
        {
            ["icon"] = new JsonObject { ["item"] = m.IconItem },
            ["title"] = new JsonObject { ["text"] = m.Title },
            ["description"] = new JsonObject { ["text"] = m.Description },
            ["frame"] = FrameName(m.Frame),
            ["show_toast"] = m.ShowToast,
            ["announce_to_chat"] = m.AnnounceToChat,
            ["hidden"] = m.Hidden
        };
        if (string.IsNullOrWhiteSpace(m.ParentId))
        {
            // "green" is our generated grass tile — it is saved into the mod's
            // own assets, so the reference points at the mod namespace.
            display["background"] = BackgroundName == "green"
                ? Namespace + ":textures/gui/advancements/backgrounds/green.png"
                : "minecraft:textures/gui/advancements/backgrounds/" + BackgroundName + ".png";
        }
        display["x"] = Math.Round(m.DisplayX, 1);
        display["y"] = Math.Round(m.DisplayY, 1);
        node["display"] = display;

        // ---- parent ----
        if (!string.IsNullOrWhiteSpace(m.ParentId))
            node["parent"] = FullId(m.ParentId);

        // ---- criteria ----
        var criteria = new JsonObject();
        var criterionName = string.IsNullOrWhiteSpace(m.Id) ? "criterion" : m.Id;
        criteria[criterionName] = BuildCriterion(m);
        node["criteria"] = criteria;

        // ---- rewards ----
        if (m.RewardExperience > 0 || !string.IsNullOrWhiteSpace(m.RewardItem))
        {
            var rewards = new JsonObject();
            if (m.RewardExperience > 0) rewards["experience"] = m.RewardExperience;
            if (!string.IsNullOrWhiteSpace(m.RewardItem))
            {
                var item = new JsonObject { ["item"] = m.RewardItem };
                if (m.RewardItemCount > 1) item["count"] = m.RewardItemCount;
                var arr = new JsonArray { item };
                rewards["loot"] = arr;
            }
            node["rewards"] = rewards;
        }

        return node;
    }

    private static JsonObject BuildCriterion(AdvancementModel m)
    {
        var c = new JsonObject { ["trigger"] = TriggerName(m.Trigger) };

        switch (m.Trigger)
        {
            case AdvancementTrigger.InventoryChanged:
            case AdvancementTrigger.ConsumeItem:
                if (!string.IsNullOrWhiteSpace(m.TriggerItem))
                {
                    var cond = new JsonObject();
                    var items = new JsonArray { new JsonObject { ["item"] = m.TriggerItem } };
                    cond["items"] = items;
                    c["conditions"] = cond;
                }
                break;

            case AdvancementTrigger.PlayerKilledEntity:
                if (!string.IsNullOrWhiteSpace(m.TriggerEntity))
                {
                    var cond = new JsonObject();
                    var entity = new JsonObject { ["type"] = m.TriggerEntity };
                    cond["entity"] = entity;
                    c["conditions"] = cond;
                }
                break;

            case AdvancementTrigger.PlayerLevel:
                var level = new JsonObject { ["level"] = new JsonObject { ["min"] = m.TriggerLevel } };
                c["conditions"] = level;
                break;

            case AdvancementTrigger.PlayerGeneratesContainerLoot:
                if (!string.IsNullOrWhiteSpace(m.TriggerLootTable))
                {
                    var cond = new JsonObject { ["loot_table"] = m.TriggerLootTable };
                    c["conditions"] = cond;
                }
                break;
        }

        return c;
    }

    /// <summary>
    /// Returns human-readable validation errors for the whole tree (Russian UI strings).
    /// Checks: unique ids, non-empty titles, no self-parenting, no cycles, valid parent refs.
    /// </summary>
    public static List<string> Validate(AdvancementDocument doc)
    {
        var errors = new List<string>();
        var models = doc.Models;

        // 1) ids unique & non-empty
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            if (string.IsNullOrWhiteSpace(m.Id))
            {
                errors.Add("У каждого достижения должен быть указан ID.");
                continue;
            }
            if (!seen.Add(m.Id))
                errors.Add($"ID «{m.Id}» используется дважды — ID должны быть уникальны.");
            if (string.IsNullOrWhiteSpace(m.Title))
                errors.Add($"Достижение «{m.Id}»: не указано название.");
            if (string.IsNullOrWhiteSpace(m.IconItem))
                errors.Add($"Достижение «{m.Id}»: не указана иконка.");
        }

        // 2) parent references: no self-parent, no cycle, must exist
        foreach (var m in models)
        {
            if (string.IsNullOrWhiteSpace(m.ParentId)) continue;
            if (string.Equals(m.ParentId, m.Id, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Достижение «{m.Id}» ссылается само на себя.");
                continue;
            }
            if (!seen.Contains(m.ParentId))
            {
                errors.Add($"Достижение «{m.Id}»: родитель «{m.ParentId}» не существует.");
                continue;
            }

            // cycle check
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { m.Id };
            var cur = m.ParentId;
            while (!string.IsNullOrWhiteSpace(cur))
            {
                if (!visited.Add(cur))
                {
                    errors.Add($"Обнаружен цикл в дереве достижений (участвует «{m.Id}»).");
                    break;
                }
                var parent = models.FirstOrDefault(x => string.Equals(x.Id, cur, StringComparison.OrdinalIgnoreCase));
                cur = parent?.ParentId ?? "";
            }
        }

        // 3) trigger-specific requirements
        foreach (var m in models)
        {
            if (m.Trigger == AdvancementTrigger.InventoryChanged && string.IsNullOrWhiteSpace(m.TriggerItem))
                errors.Add($"«{m.Id}»: для триггера «получить предмет» укажите предмет.");
            if (m.Trigger == AdvancementTrigger.ConsumeItem && string.IsNullOrWhiteSpace(m.TriggerItem))
                errors.Add($"«{m.Id}»: для триггера «съесть предмет» укажите предмет.");
            if (m.Trigger == AdvancementTrigger.PlayerKilledEntity && string.IsNullOrWhiteSpace(m.TriggerEntity))
                errors.Add($"«{m.Id}»: для триггера «убить моба» укажите моба.");
            if (m.Trigger == AdvancementTrigger.PlayerGeneratesContainerLoot && string.IsNullOrWhiteSpace(m.TriggerLootTable))
                errors.Add($"«{m.Id}»: для триггера «открыть сундук» укажите loot table.");
        }

        return errors;
    }

    public static string TriggerName(AdvancementTrigger t) => t switch
    {
        AdvancementTrigger.InventoryChanged => "minecraft:inventory_changed",
        AdvancementTrigger.PlayerKilledEntity => "minecraft:player_killed_entity",
        AdvancementTrigger.PlayerLevel => "minecraft:player_level",
        AdvancementTrigger.PlayerGeneratesContainerLoot => "minecraft:player_generates_container_loot",
        AdvancementTrigger.BrewedPotion => "minecraft:brewed_potion",
        AdvancementTrigger.ConstructBeacon => "minecraft:construct_beacon",
        AdvancementTrigger.ConsumeItem => "minecraft:consume_item",
        AdvancementTrigger.UsedTotem => "minecraft:used_totem",
        AdvancementTrigger.Tick => "minecraft:tick",
        AdvancementTrigger.Impossible => "minecraft:impossible",
        _ => "minecraft:impossible"
    };

    public static AdvancementTrigger TriggerFromName(string name) => name switch
    {
        "minecraft:inventory_changed" => AdvancementTrigger.InventoryChanged,
        "minecraft:player_killed_entity" => AdvancementTrigger.PlayerKilledEntity,
        "minecraft:player_level" => AdvancementTrigger.PlayerLevel,
        "minecraft:player_generates_container_loot" => AdvancementTrigger.PlayerGeneratesContainerLoot,
        "minecraft:brewed_potion" => AdvancementTrigger.BrewedPotion,
        "minecraft:construct_beacon" => AdvancementTrigger.ConstructBeacon,
        "minecraft:consume_item" => AdvancementTrigger.ConsumeItem,
        "minecraft:used_totem" => AdvancementTrigger.UsedTotem,
        "minecraft:tick" => AdvancementTrigger.Tick,
        "minecraft:impossible" => AdvancementTrigger.Impossible,
        _ => AdvancementTrigger.Impossible
    };

    private static string FrameName(AdvancementFrame f) => f switch
    {
        AdvancementFrame.Task => "task",
        AdvancementFrame.Goal => "goal",
        AdvancementFrame.Challenge => "challenge",
        _ => "task"
    };

    public static AdvancementFrame FrameFromName(string name) => name switch
    {
        "goal" => AdvancementFrame.Goal,
        "challenge" => AdvancementFrame.Challenge,
        _ => AdvancementFrame.Task
    };

    /// <summary>"id" -> "namespace:id".</summary>
    public static string FullId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;
        return id.Contains(':') ? id : Namespace + ":" + id;
    }

    /// <summary>Strips the namespace: "mine_ide:foo" -> "foo".</summary>
    public static string ShortId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;
        int i = id.IndexOf(':');
        return i >= 0 ? id[(i + 1)..] : id;
    }
}
