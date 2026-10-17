using System;
using System.Collections.Generic;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MultiEnchantmentCoopFix;

/// <summary>
/// MultiEnchantmentMod records the order in which enchantments were applied to a card in an in-memory table
/// (ConditionalWeakTable keyed by the live CardModel) and writes it into <c>SerializableCard.Props</c> as the
/// string property "MultiEnchantmentOrderData" (a JSON list of enchantment ModelIds) inside its
/// <c>CardModel.ToSerializable</c> postfix.
///
/// In co-op, <c>SerializableCard</c> is not only the save format — it is what peers exchange before every combat
/// (<c>CombatStateSynchronizer</c> → <c>Player.SyncWithSerializedPlayer</c> rebuilds remote decks from it) and
/// what <c>ChecksumTracker</c> hashes to detect divergence. Anything that is present on one peer and absent on the
/// other therefore kicks the client with "State divergence detected".
///
/// For a card that carries a single enchantment and no extra enchantments the order is trivially [that enchantment]:
/// MultiEnchantmentMod itself re-derives it (<c>SeedMissingApplicationOrder</c>) whenever it is missing. So we
/// remove it from the serialised form. That makes a vanilla-enchanted card (Tezcatara's Ember, Mystic Lighter, ...)
/// serialise exactly like vanilla on every peer, regardless of which peer performed the enchant or which peer
/// rebuilt the card from the wire.
/// Also removes orphaned order metadata when no enchantments remain, e.g. after Primal Force transforms
/// a Spiral-enchanted attack into an incompatible Giant Rock. MultiEnchantmentMod copies the source order
/// even when it rejects the enchantment on the replacement.
///
/// Cards that truly carry multi-enchantment state ("MultiEnchantmentData" present, or a non-trivial order) are left
/// untouched — that state is gameplay-relevant and must travel.
/// </summary>
[HarmonyPatch]
internal static class SerializationPatches
{
    internal const string DataPropertyName = "MultiEnchantmentData";
    internal const string OrderPropertyName = "MultiEnchantmentOrderData";

    private static bool _loggedFirstStrip;

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(ModMain.MultiEnchantmentHarmonyId)]
    private static void ToSerializablePostfix(CardModel __instance, ref SerializableCard __result)
    {
        if (!ModMain.StripRedundantOrderData || __result == null)
        {
            return;
        }
        try
        {
            bool stripped = StripRedundantOrderData(__result, __instance?.Enchantment?.Id);
            // Always collapse empty property lists to the vanilla `null` shape so both peers serialise identically.
            NormalizeEmpty(__result);
            if (stripped && (ModMain.VerboseLog || !_loggedFirstStrip))
            {
                _loggedFirstStrip = true;
                ModMain.Log.Info($"[MECoopFix] Stripped redundant {OrderPropertyName} from serialised card {__result.Id} " +
                                 "(order derivable or no enchantments remain). Further strips are logged only with verboseLog=true.");
            }
        }
        catch (Exception ex)
        {
            ModMain.Log.Warn($"[MECoopFix] ToSerializable postfix failed for card {__result?.Id}; leaving card untouched. {ex}");
        }
    }

    /// <summary>
    /// Pure logic, exposed for tests. Returns true if a property was removed.
    /// </summary>
    internal static bool StripRedundantOrderData(SerializableCard card, ModelId? primaryEnchantmentId)
    {
        SavedProperties? props = card.Props;
        List<SavedProperties.SavedProperty<string>>? strings = props?.strings;
        if (props == null || strings == null || strings.Count == 0)
        {
            return false;
        }

        // Extra enchantments present → real multi-enchantment state; the order is meaningful. Leave everything.
        int dataIndex = strings.FindIndex(p => string.Equals(p.name, DataPropertyName, StringComparison.Ordinal));
        if (dataIndex >= 0 && !string.IsNullOrWhiteSpace(strings[dataIndex].value))
        {
            return false;
        }

        int orderIndex = strings.FindIndex(p => string.Equals(p.name, OrderPropertyName, StringComparison.Ordinal));
        if (orderIndex < 0)
        {
            return false;
        }

        // The serialized primary is a defensive fallback: never treat an enchanted payload as unenchanted.
        if (!IsTrivialOrder(strings[orderIndex].value, primaryEnchantmentId ?? card.Enchantment?.Id))
        {
            return false;
        }

        strings.RemoveAt(orderIndex);
        NormalizeEmpty(card);
        return true;
    }

    /// <summary>
    /// The order is redundant when it is empty, no enchantments remain, or it consists of exactly the primary.
    /// The caller has already ruled out additional enchantment data.
    /// Anything we cannot parse is treated as non-trivial (kept) — never guess.
    /// </summary>
    internal static bool IsTrivialOrder(string? orderJson, ModelId? primaryEnchantmentId)
    {
        if (string.IsNullOrWhiteSpace(orderJson))
        {
            return true;
        }
        List<ModelId>? order;
        try
        {
            order = JsonSerializer.Deserialize<List<ModelId>>(orderJson);
        }
        catch (JsonException)
        {
            return false;
        }
        if (order == null || order.Count == 0)
        {
            return true;
        }
        if (primaryEnchantmentId == null)
        {
            // A parsed history cannot affect enchantment ordering when the card has no enchantments.
            return true;
        }
        if (order.Count == 1 && primaryEnchantmentId != null)
        {
            return primaryEnchantmentId.Equals(order[0]);
        }
        return false;
    }

    /// <summary>
    /// Vanilla <c>ToSerializable</c> yields <c>Props == null</c> when a card has nothing to save. An empty
    /// non-null list serialises differently from null (<c>writer.WriteBool(strings != null)</c>), so after
    /// removing the last entry we must collapse back to exactly the vanilla shape.
    /// </summary>
    internal static void NormalizeEmpty(SerializableCard card)
    {
        SavedProperties? props = card.Props;
        if (props == null)
        {
            return;
        }
        if (props.strings != null && props.strings.Count == 0) props.strings = null;
        if (props.ints != null && props.ints.Count == 0) props.ints = null;
        if (props.bools != null && props.bools.Count == 0) props.bools = null;
        if (props.intArrays != null && props.intArrays.Count == 0) props.intArrays = null;
        if (props.modelIds != null && props.modelIds.Count == 0) props.modelIds = null;
        if (props.cards != null && props.cards.Count == 0) props.cards = null;
        if (props.cardArrays != null && props.cardArrays.Count == 0) props.cardArrays = null;
        if (props.strings == null && props.ints == null && props.bools == null && props.intArrays == null &&
            props.modelIds == null && props.cards == null && props.cardArrays == null)
        {
            card.Props = null;
        }
    }
}
