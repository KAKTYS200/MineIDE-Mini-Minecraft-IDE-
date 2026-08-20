using System;
using System.Linq;
using System.Text.Json.Nodes;
using MineIDE.AdvancementEditor.Models;

namespace MineIDE.AdvancementEditor.Services;

/// <summary>
/// Parses a Minecraft 1.20.1 advancement JSON file back into a <see cref="AdvancementModel"/>.
/// Each advancement file holds exactly one node (plus its "parent" reference),
/// so a single file maps to a single model. UI-free.
/// </summary>
public static class AdvancementJsonParser
{
    public static AdvancementModel Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new FormatException("Корень JSON должен быть объектом.");

        var m = new AdvancementModel();
        if (root["display"] is JsonObject display)
            ParseDisplay(display, m);

        m.ParentId = root["parent"]?.GetValue<string>() ?? "";

        if (root["criteria"] is JsonObject criteria)
        {
            var first = criteria.FirstOrDefault();
            if (first.Value is JsonObject criterion)
            {
                var triggerName = criterion["trigger"]?.GetValue<string>() ?? "";
                m.Trigger = AdvancementJsonGenerator.TriggerFromName(triggerName);

                if (criterion["conditions"] is JsonObject cond)
                    ParseConditions(cond, m);
            }
        }

        if (root["rewards"] is JsonObject rewards)
            ParseRewards(rewards, m);

        return m;
    }

    private static void ParseDisplay(JsonObject display, AdvancementModel m)
    {
        if (display["icon"] is JsonObject icon)
            m.IconItem = icon["item"]?.GetValue<string>() ?? "minecraft:stone";

        if (display["title"] is JsonObject title)
            m.Title = TextOf(title);
        if (display["description"] is JsonObject desc)
            m.Description = TextOf(desc);

        m.Frame = AdvancementJsonGenerator.FrameFromName(display["frame"]?.GetValue<string>() ?? "task");
        m.ShowToast = display["show_toast"]?.GetValue<bool>() ?? true;
        m.AnnounceToChat = display["announce_to_chat"]?.GetValue<bool>() ?? true;
        m.Hidden = display["hidden"]?.GetValue<bool>() ?? false;

        if (display["x"] is JsonValue xv && xv.TryGetValue<double>(out var x)) m.DisplayX = x;
        if (display["y"] is JsonValue yv && yv.TryGetValue<double>(out var y)) m.DisplayY = y;
    }

    private static string TextOf(JsonObject node)
    {
        if (node["text"] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (node["translate"] is JsonValue tv && tv.TryGetValue<string>(out var ts)) return ts;
        return "";
    }

    private static void ParseConditions(JsonObject cond, AdvancementModel m)
    {
        switch (m.Trigger)
        {
            case AdvancementTrigger.InventoryChanged:
            case AdvancementTrigger.ConsumeItem:
                if (cond["items"] is JsonArray items && items.Count > 0 && items[0] is JsonObject first)
                    m.TriggerItem = first["item"]?.GetValue<string>() ?? "";
                break;

            case AdvancementTrigger.PlayerKilledEntity:
                if (cond["entity"] is JsonObject entity)
                    m.TriggerEntity = entity["type"]?.GetValue<string>() ?? "";
                break;

            case AdvancementTrigger.PlayerLevel:
                if (cond["level"] is JsonObject level && level["min"] is JsonValue min &&
                    min.TryGetValue<int>(out var lvl))
                    m.TriggerLevel = lvl;
                break;

            case AdvancementTrigger.PlayerGeneratesContainerLoot:
                m.TriggerLootTable = cond["loot_table"]?.GetValue<string>() ?? "";
                break;
        }
    }

    private static void ParseRewards(JsonObject rewards, AdvancementModel m)
    {
        if (rewards["experience"] is JsonValue exp && exp.TryGetValue<int>(out var x))
            m.RewardExperience = x;

        if (rewards["loot"] is JsonArray loot && loot.Count > 0 && loot[0] is JsonObject first)
        {
            m.RewardItem = first["item"]?.GetValue<string>() ?? "";
            if (first["count"] is JsonValue c && c.TryGetValue<int>(out var n))
                m.RewardItemCount = n;
        }
    }
}
