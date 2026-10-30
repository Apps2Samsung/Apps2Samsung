using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Apps2Samsung.Remote
{
    /// <summary>
    /// One app's on-screen keyboard in the shape a person can edit: rows as plain text, plus the cells
    /// whose label is an action rather than a character.
    /// <code>
    /// { "id": "youtube-search", "appId": "9Ur5IzDKqV.TizenYouTube",
    ///   "rows": ["abcdefg", "hijklmn", "opqrstu", "vwxyz-'", ""],
    ///   "cells": { "DELETE": [0,7], "SPACE": [4,0], "CLEAR": [4,1], "SUBMIT": [4,2] },
    ///   "opensOn": [0,0], "homeKeys": ["KEY_UP", "KEY_UP", "KEY_UP", "KEY_UP", "KEY_UP", "KEY_UP", "KEY_DOWN"],
    ///   "wrapHorizontal": false, "columnResetRows": [4] }
    /// </code>
    /// Rows are written as the letters/symbols the cells type, in order; an action cell lives in
    /// <c>cells</c> and its column is padded into the row automatically, so the text stays readable.
    /// </summary>
    public static class SamsungRemoteKeyboardJson
    {
        /// <summary>Filler for a column that only exists to hold an action cell.</summary>
        public const string PadCell = "·";

        /// <summary>The profile as the friendly JSON above. Round-trips through <see cref="TryParse"/>.</summary>
        public static string ToJson(SamsungRemoteKeyboardProfile profile)
        {
            // Action cells live in `cells` on the way out; in the rows they are replaced by padding so
            // their column survives, and the padding is then trimmed from the end so the row reads as
            // the plain text a person would type ("abcdefg", not "abcdefgDELETE").
            var actionTokens = new HashSet<string>(profile.Cells.Keys, StringComparer.Ordinal);
            var rows = new List<string>();
            for (var row = 0; row < profile.Rows.Count; row++)
            {
                var cells = profile.Rows[row]
                    .Select(cell => actionTokens.Contains(cell) ? PadCell : cell)
                    .ToList();
                while (cells.Count > 0 && cells[cells.Count - 1] == PadCell)
                    cells.RemoveAt(cells.Count - 1);
                rows.Add(string.Concat(cells));
            }

            var named = new JsonObject();
            foreach (var cell in profile.Cells)
                named[cell.Key] = new JsonArray(cell.Value.Row, cell.Value.Col);

            var json = new JsonObject
            {
                ["id"] = profile.Id,
                ["label"] = profile.Label,
                ["appId"] = profile.AppId,
                ["rows"] = new JsonArray(rows.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["cells"] = named,
                ["opensOn"] = new JsonArray(profile.OpensOn.Row, profile.OpensOn.Col),
                ["homeKeys"] = new JsonArray(profile.HomeKeys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                ["wrapHorizontal"] = profile.WrapHorizontal,
                ["columnResetRows"] = new JsonArray(profile.ColumnResetRows.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            };

            if (profile.DirectKeys.Count > 0)
            {
                var direct = new JsonObject();
                foreach (var key in profile.DirectKeys)
                    direct[key.Key.ToString()] = key.Value;
                json["directKeys"] = direct;
            }

            // Relaxed escaping: this text goes into a box a person edits, so `'` must stay `'` and not
            // become \u0027.
            return json.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }

        /// <summary>
        /// Parses a profile, reporting why it was rejected rather than throwing: this text comes from a
        /// user pasting into a box, so the message is shown to them.
        /// </summary>
        public static bool TryParse(string? json, out SamsungRemoteKeyboardProfile? profile, out string? error)
        {
            profile = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the profile is empty";
                return false;
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                error = $"not valid JSON: {ex.Message}";
                return false;
            }

            if (root is not JsonObject obj)
            {
                error = "the profile must be a JSON object";
                return false;
            }

            var rows = new List<List<string>>();
            var rowTexts = obj["rows"] as JsonArray;
            if (rowTexts is null || rowTexts.Count == 0)
            {
                error = "\"rows\" must be a non-empty array of strings";
                return false;
            }

            foreach (var rowNode in rowTexts)
            {
                var text = rowNode?.GetValue<string>() ?? string.Empty;
                rows.Add(text.Select(c => c.ToString()).ToList());
            }

            var cells = new Dictionary<string, (int Row, int Col)>(StringComparer.OrdinalIgnoreCase);
            if (obj["cells"] is JsonObject cellNodes)
            {
                foreach (var entry in cellNodes)
                {
                    if (entry.Value is not JsonArray pair || pair.Count != 2)
                    {
                        error = $"cell \"{entry.Key}\" must be [row, column]";
                        return false;
                    }

                    var row = pair[0]?.GetValue<int>() ?? -1;
                    var col = pair[1]?.GetValue<int>() ?? -1;
                    if (row < 0 || row >= rows.Count || col < 0)
                    {
                        error = $"cell \"{entry.Key}\" points at row {row} column {col}, which is not on the grid";
                        return false;
                    }

                    // Pad the row so the action cell has a position to live in.
                    while (rows[row].Count <= col)
                        rows[row].Add(PadCell);

                    var name = NormaliseCellName(entry.Key);
                    cells[name] = (row, col);
                    // Put the token back in the cell so the in-memory grid is canonical: a plan, a test
                    // or a human reading Rows[4][0] sees SPACE, not a padding marker.
                    rows[row][col] = name;
                }
            }

            if (obj["opensOn"] is not JsonArray opensOnPair || opensOnPair.Count != 2)
            {
                error = "\"opensOn\" must be [row, column]";
                return false;
            }

            var opensOn = (Row: opensOnPair[0]?.GetValue<int>() ?? -1, Col: opensOnPair[1]?.GetValue<int>() ?? -1);
            if (opensOn.Row < 0 || opensOn.Row >= rows.Count || opensOn.Col < 0 || opensOn.Col >= rows[opensOn.Row].Count)
            {
                error = $"\"opensOn\" {opensOn.Row},{opensOn.Col} is not a cell on the grid";
                return false;
            }

            var homeKeys = new List<string>();
            if (obj["homeKeys"] is JsonArray keyNodes)
            {
                foreach (var keyNode in keyNodes)
                {
                    var key = keyNode?.GetValue<string>() ?? string.Empty;
                    if (!key.StartsWith("KEY_", StringComparison.Ordinal))
                    {
                        error = $"homeKeys entry \"{key}\" is not a key code (expected something like KEY_UP)";
                        return false;
                    }

                    homeKeys.Add(key);
                }
            }

            var resetRows = new List<int>();
            if (obj["columnResetRows"] is JsonArray resetNodes)
            {
                foreach (var node in resetNodes)
                {
                    var row = node?.GetValue<int>() ?? -1;
                    if (row < 0 || row >= rows.Count)
                    {
                        error = $"columnResetRows entry {row} is not a row on the grid";
                        return false;
                    }

                    resetRows.Add(row);
                }
            }

            var directKeys = new Dictionary<char, string>();
            if (obj["directKeys"] is JsonObject directNodes)
            {
                foreach (var entry in directNodes)
                {
                    if (entry.Key.Length != 1)
                    {
                        error = $"directKeys key \"{entry.Key}\" must be a single character";
                        return false;
                    }

                    directKeys[entry.Key[0]] = entry.Value?.GetValue<string>() ?? string.Empty;
                }
            }

            var built = new SamsungRemoteKeyboardProfile(
                Id: obj["id"]?.GetValue<string>() ?? "custom",
                Rows: rows.Select(r => (IReadOnlyList<string>)r).ToList(),
                Cells: cells,
                OpensOn: opensOn,
                HomeKeys: homeKeys,
                WrapHorizontal: obj["wrapHorizontal"]?.GetValue<bool>() ?? false)
            {
                AppId = obj["appId"]?.GetValue<string>(),
                Label = obj["label"]?.GetValue<string>(),
                DirectKeys = directKeys,
                ColumnResetRows = resetRows,
            };

            // The one invariant every plan depends on: the grid must not be empty.
            if (built.Rows.Any(r => r.Count == 0))
            {
                error = "every row needs at least one cell (a row of \"\" is only allowed with a cell in it)";
                return false;
            }

            profile = built;
            return true;
        }

        private static string NormaliseCellName(string name) => name.ToUpperInvariant() switch
        {
            "SPACE" => SamsungRemoteKeyboard.SpaceCell,
            "DELETE" or "BACKSPACE" => SamsungRemoteKeyboard.DeleteCell,
            "CLEAR" => SamsungRemoteKeyboard.ClearCell,
            "SUBMIT" or "SEARCH" => SamsungRemoteKeyboard.SubmitCell,
            var other => other,
        };
    }
}
