using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using MineIDE.RecipeEditor.Models;

namespace MineIDE.RecipeEditor.Services;

/// <summary>
/// Parses a Minecraft recipe JSON back into a <see cref="RecipeDocument"/>.
/// Supports every kind the generator emits, plus the legacy smithing type.
/// Kept free of any UI/WPF dependency.
/// </summary>
public static class RecipeJsonParser
{
    public static RecipeDocument Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new FormatException("Корень JSON должен быть объектом.");

        var type = root["type"]?.GetValue<string>() ?? "";
        var doc = new RecipeDocument();

        switch (type)
        {
            case "minecraft:crafting_shaped":
                doc.Kind = RecipeKind.Shaped;
                ParseShaped(root, doc);
                break;

            case "minecraft:crafting_shapeless":
                doc.Kind = RecipeKind.Shapeless;
                ParseShapeless(root, doc);
                break;

            case "minecraft:smelting":
            case "minecraft:blasting":
            case "minecraft:smoking":
            case "minecraft:campfire_cooking":
                doc.Kind = type switch
                {
                    "minecraft:blasting" => RecipeKind.Blasting,
                    "minecraft:smoking" => RecipeKind.Smoking,
                    "minecraft:campfire_cooking" => RecipeKind.Campfire,
                    _ => RecipeKind.Smelting
                };
                ParseFurnace(root, doc);
                break;

            case "minecraft:stonecutting":
                doc.Kind = RecipeKind.Stonecutting;
                ParseStonecutting(root, doc);
                break;

            case "minecraft:smithing_transform":
            case "minecraft:smithing":
                doc.Kind = RecipeKind.Smithing;
                ParseSmithing(root, doc);
                break;

            default:
                throw new FormatException("Неизвестный тип рецепта: " + (string.IsNullOrEmpty(type) ? "(пусто)" : type));
        }

        doc.Group = root["group"]?.GetValue<string>() ?? "";
        return doc;
    }

    private static void ReadItem(JsonNode? node, RecipeItem target)
    {
        if (node is not JsonObject o) return;
        target.Id = o["item"]?.GetValue<string>() ?? "";
        target.Count = o["count"] is JsonValue cv && cv.TryGetValue<int>(out var n) ? n : 1;
        target.Nbt = o["nbt"]?.GetValue<string>() ?? "";
    }

    private static void ReadResult(JsonObject root, RecipeDocument doc)
        => ReadItem(root["result"], doc.Result);

    private static void ParseShaped(JsonObject root, RecipeDocument doc)
    {
        var key = new Dictionary<char, RecipeItem>();
        if (root["key"] is JsonObject keyObj)
        {
            foreach (var kv in keyObj)
            {
                if (kv.Key is not { Length: 1 }) continue;
                var item = new RecipeItem();
                ReadItem(kv.Value, item);
                key[kv.Key[0]] = item;
            }
        }

        if (root["pattern"] is JsonArray pattern)
        {
            for (int r = 0; r < 3 && r < pattern.Count; r++)
            {
                var row = pattern[r]?.GetValue<string>() ?? "";
                for (int c = 0; c < 3; c++)
                {
                    if (c >= row.Length) continue;
                    char ch = row[c];
                    if (ch == ' ' || !key.TryGetValue(ch, out var src)) continue;
                    var cell = doc.Grid[r * 3 + c];
                    cell.Id = src.Id;
                    cell.Count = src.Count;
                    cell.Nbt = src.Nbt;
                }
            }
        }
        ReadResult(root, doc);
    }

    private static void ParseShapeless(JsonObject root, RecipeDocument doc)
    {
        int index = 0;
        if (root["ingredients"] is JsonArray ingredients)
        {
            foreach (var node in ingredients)
            {
                if (index >= 9) break;
                ReadItem(node, doc.Grid[index++]);
            }
        }
        ReadResult(root, doc);
    }

    private static void ParseFurnace(JsonObject root, RecipeDocument doc)
    {
        ReadItem(root["ingredient"], doc.Ingredient);
        ReadResult(root, doc);
        if (root["experience"] is JsonValue ev && ev.TryGetValue<double>(out var exp)) doc.Experience = exp;
        if (root["cookingtime"] is JsonValue tv && tv.TryGetValue<int>(out var t)) doc.CookingTime = t;
    }

    private static void ParseStonecutting(JsonObject root, RecipeDocument doc)
    {
        ReadItem(root["ingredient"], doc.Ingredient);
        ReadResult(root, doc);
    }

    private static void ParseSmithing(JsonObject root, RecipeDocument doc)
    {
        ReadItem(root["template"], doc.Template);
        ReadItem(root["base"], doc.Base);
        ReadItem(root["addition"], doc.Addition);
        ReadResult(root, doc);
    }
}
