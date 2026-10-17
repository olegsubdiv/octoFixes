using System;
using System.Collections.Generic;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;
using MultiEnchantmentCoopFix;

// Minimal self-checking harness (no test framework needed): exercises the pure logic of the companion mod
// against the real game types from sts2.dll. Run with `dotnet run -c Release` from the tests folder.
internal static class Program
{
    private static int _failed;

    private static int Main()
    {
        // The combined assembly must have one entry point; module startup must not double-patch Hextech.
        int initializers = 0;
        foreach (Type type in typeof(OctoFixes.EntryPoint).Assembly.GetTypes())
        {
            foreach (var attribute in type.GetCustomAttributesData())
            {
                if (attribute.AttributeType.Name == "ModInitializerAttribute") initializers++;
            }
        }
        Check("combined assembly has exactly one game initializer", initializers == 1);
        Check("module Harmony owners are distinct", ModMain.HarmonyId != HextechFormRuneCoopFix.ModMain.HarmonyId);
        ModelId ember = new ModelId("ENCHANTMENT", "TEZCATARAS_EMBER");
        ModelId other = new ModelId("ENCHANTMENT", "SEBASTIAN_CHARMED");
        string orderEmber = JsonSerializer.Serialize(new List<ModelId> { ember });
        Console.WriteLine("OrderData sample: " + orderEmber);

        // 1. The exact shape from the divergence dump: single vanilla enchantment, OrderData=[EMBER] -> stripped, Props -> null.
        {
            SerializableCard card = Card(orderEmber);
            bool stripped = SerializationPatches.StripRedundantOrderData(card, ember);
            Check("dump shape stripped", stripped);
            Check("dump shape props null", card.Props == null);
        }
        // 2. Order does not match the primary enchantment -> kept.
        {
            SerializableCard card = Card(orderEmber);
            Check("mismatched primary kept", !SerializationPatches.StripRedundantOrderData(card, other));
            Check("mismatched primary props intact", card.Props?.strings?.Count == 1);
        }
        // 3. Two entries (real multi-enchant order) -> kept.
        {
            SerializableCard card = Card(JsonSerializer.Serialize(new List<ModelId> { ember, other }));
            Check("multi order kept", !SerializationPatches.StripRedundantOrderData(card, ember));
        }
        // 4. Extras present (MultiEnchantmentData) -> nothing touched even if order is trivial.
        {
            SerializableCard card = Card(orderEmber);
            card.Props!.strings!.Add(new SavedProperties.SavedProperty<string>("MultiEnchantmentData", "[{\"Id\":\"x\"}]"));
            Check("extras present kept", !SerializationPatches.StripRedundantOrderData(card, ember));
            Check("extras present count 2", card.Props?.strings?.Count == 2);
        }
        // 5. No primary enchantment but empty order -> stripped.
        {
            SerializableCard card = Card("[]");
            Check("empty order stripped", SerializationPatches.StripRedundantOrderData(card, null));
            Check("empty order props null", card.Props == null);
        }
        // 6. Garbage JSON -> kept (never guess).
        {
            SerializableCard card = Card("not json");
            Check("garbage kept", !SerializationPatches.StripRedundantOrderData(card, ember));
        }
        // 7. Other props survive; only OrderData removed; Props stays non-null.
        {
            SerializableCard card = Card(orderEmber);
            card.Props!.ints = new List<SavedProperties.SavedProperty<int>> { new SavedProperties.SavedProperty<int>("SomeVanillaCounter", 3) };
            Check("mixed stripped", SerializationPatches.StripRedundantOrderData(card, ember));
            Check("mixed strings null", card.Props?.strings == null);
            Check("mixed ints kept", card.Props?.ints?.Count == 1);
        }
        // 8. NormalizeEmpty collapses empty lists -> null Props.
        {
            SerializableCard card = new SerializableCard
            {
                Id = new ModelId("CARD", "STRIKE_SILENT"),
                Props = new SavedProperties { strings = new List<SavedProperties.SavedProperty<string>>(), ints = new List<SavedProperties.SavedProperty<int>>() },
            };
            SerializationPatches.NormalizeEmpty(card);
            Check("normalize empty", card.Props == null);
        }
        // 9. FoldTable is deterministic and order-sensitive.
        {
            List<string> a = new List<string> { "MultiEnchantmentCardInstanceId", "MultiEnchantmentData", "AttacksPlayed" };
            List<string> b = new List<string> { "MultiEnchantmentData", "MultiEnchantmentCardInstanceId", "AttacksPlayed" };
            Check("fold deterministic", PropertyTablePatches.FoldTable(615452784u, a, 7) == PropertyTablePatches.FoldTable(615452784u, a, 7));
            Check("fold order-sensitive", PropertyTablePatches.FoldTable(615452784u, a, 7) != PropertyTablePatches.FoldTable(615452784u, b, 7));
            Check("fold bitsize-sensitive", PropertyTablePatches.FoldTable(615452784u, a, 7) != PropertyTablePatches.FoldTable(615452784u, a, 6));
        }
        // 9b. CanonicalizeTable: real host order [EndlessMode x5, MultiEnchantment x7, base...] and the client's
        //     [MultiEnchantment x7, EndlessMode x5, base...] converge to the same table and consistent maps.
        {
            List<string> host = new List<string> { "IsMelted", "IsWax", "SavedEffectPercent", "SavedFloorsBeforeLoop", "SavedStackCount",
                "MultiEnchantmentCardInstanceId", "MultiEnchantmentData", "MultiEnchantmentEnchantmentStatus", "MultiEnchantmentInstanceId",
                "MultiEnchantmentMergedStackAmounts", "MultiEnchantmentOrderData", "MultiEnchantmentScopeData", "IsMultiplayer", "AttacksPlayed" };
            List<string> client = new List<string> { "MultiEnchantmentCardInstanceId", "MultiEnchantmentData", "MultiEnchantmentEnchantmentStatus",
                "MultiEnchantmentInstanceId", "MultiEnchantmentMergedStackAmounts", "MultiEnchantmentOrderData", "MultiEnchantmentScopeData",
                "IsMelted", "IsWax", "SavedEffectPercent", "SavedFloorsBeforeLoop", "SavedStackCount", "IsMultiplayer", "AttacksPlayed" };
            Dictionary<string, int> hostMap = new Dictionary<string, int>();
            for (int i = 0; i < host.Count; i++) hostMap[host[i]] = i;
            Dictionary<string, int> clientMap = new Dictionary<string, int>();
            for (int i = 0; i < client.Count; i++) clientMap[client[i]] = i;
            Check("pre-canonical: client OrderData index == host CardInstanceId index (the observed bug)",
                clientMap["MultiEnchantmentOrderData"] == hostMap["MultiEnchantmentCardInstanceId"]);
            PropertyTablePatches.CanonicalizeTable(host, hostMap);
            PropertyTablePatches.CanonicalizeTable(client, clientMap);
            bool same = host.Count == client.Count;
            for (int i = 0; same && i < host.Count; i++)
            {
                same = host[i] == client[i] && hostMap[host[i]] == i && clientMap[client[i]] == i;
            }
            Check("canonical tables identical and maps consistent", same);
            Check("canonical fold identical", PropertyTablePatches.FoldTable(1u, host, 4) == PropertyTablePatches.FoldTable(1u, client, 4));
            Check("canonicalize idempotent", PropertyTablePatches.CanonicalizeTable(host, hostMap) == 0);
        }
        // 10. Idempotent: applying twice equals applying once.
        {
            SerializableCard card = Card(orderEmber);
            SerializationPatches.StripRedundantOrderData(card, ember);
            Check("idempotent second call no-op", !SerializationPatches.StripRedundantOrderData(card, ember));
        }

        // 11. FoldPools: deterministic; sensitive to a member missing from a pool (TheHeroExpansion toggle case) and to CanBeGeneratedInCombat.
        {
            string repurpose = ContentPoolPatches.Member(new ModelId("CARD", "THEHEROEXPANSION-REPURPOSE"), true);
            List<string> full = new List<string> { "CARD.ACCURACY", "CARD.BACKFLIP", repurpose };
            List<string> hostSide = new List<string> { "CARD.ACCURACY", "CARD.BACKFLIP" };
            List<string> nogen = new List<string> { "CARD.ACCURACY", "CARD.BACKFLIP", ContentPoolPatches.Member(new ModelId("CARD", "THEHEROEXPANSION-REPURPOSE"), false) };
            var a = new List<ContentPoolPatches.PoolDigest> { new("CARD_POOL.SILENT", full) };
            var b = new List<ContentPoolPatches.PoolDigest> { new("CARD_POOL.SILENT", hostSide) };
            var c = new List<ContentPoolPatches.PoolDigest> { new("CARD_POOL.SILENT", nogen) };
            Check("pools fold deterministic", ContentPoolPatches.FoldPools(7u, a) == ContentPoolPatches.FoldPools(7u, a));
            Check("pools fold detects missing member", ContentPoolPatches.FoldPools(7u, a) != ContentPoolPatches.FoldPools(7u, b));
            Check("pools fold detects nogen flag", ContentPoolPatches.FoldPools(7u, a) != ContentPoolPatches.FoldPools(7u, c));
            Check("modded member detected", ContentPoolPatches.IsModded(repurpose) && !ContentPoolPatches.IsModded("CARD.ACCURACY"));
        }

        // 12. Regression: checksum 342 (2026-10-08), Primal Force -> unenchanted Giant Rock.
        {
            var spiral = new ModelId("ENCHANTMENT", "SPIRAL");
            SerializableCard host = Card("[]");
            host.Id = new ModelId("CARD", "GIANT_ROCK");
            host.Enchantment = null;
            host.Props = null;
            SerializableCard client = Card(JsonSerializer.Serialize(new List<ModelId> { spiral }));
            client.Id = host.Id;
            client.Enchantment = null;
            Check("Giant Rock orphaned Spiral order stripped", SerializationPatches.StripRedundantOrderData(client, null));
            Check("Giant Rock host/client payloads converge", JsonSerializer.Serialize(host) == JsonSerializer.Serialize(client));
            Check("orphan cleanup idempotent", !SerializationPatches.StripRedundantOrderData(client, null));
        }
        // 13. Orphaned histories with multiple entries are also redundant; other metadata survives.
        {
            SerializableCard card = Card(JsonSerializer.Serialize(new List<ModelId> { ember, other }));
            card.Enchantment = null;
            card.Props!.ints = new List<SavedProperties.SavedProperty<int>> { new("SomeVanillaCounter", 3) };
            Check("multi-entry orphan stripped", SerializationPatches.StripRedundantOrderData(card, null));
            Check("orphan cleanup preserves other properties", card.Props?.strings == null && card.Props?.ints?[0].value == 3);
        }
        // 14. Additional enchantments remain gameplay state even without a primary.
        {
            SerializableCard card = Card(orderEmber);
            card.Enchantment = null;
            card.Props!.strings!.Add(new SavedProperties.SavedProperty<string>("MultiEnchantmentData", "[{\"Id\":\"x\"}]"));
            Check("extras without primary kept", !SerializationPatches.StripRedundantOrderData(card, null));
            Check("extras without primary payload intact", card.Props?.strings?.Count == 2);
        }
        // 15. Preserve unknown/malformed data and fall back to the serialized primary when provided.
        {
            SerializableCard card = Card("not json");
            card.Enchantment = null;
            Check("malformed orphan history kept", !SerializationPatches.StripRedundantOrderData(card, null));
            card = Card(JsonSerializer.Serialize(new List<ModelId> { other }));
            Check("serialized primary prevents orphan cleanup", !SerializationPatches.StripRedundantOrderData(card, null));
        }

        Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : _failed + " TEST(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static SerializableCard Card(string orderJson)
    {
        return new SerializableCard
        {
            Id = new ModelId("CARD", "STRIKE_SILENT"),
            Enchantment = new SerializableEnchantment { Id = new ModelId("ENCHANTMENT", "TEZCATARAS_EMBER"), Amount = 1 },
            Props = new SavedProperties
            {
                strings = new List<SavedProperties.SavedProperty<string>>
                {
                    new SavedProperties.SavedProperty<string>("MultiEnchantmentOrderData", orderJson),
                },
            },
            FloorAddedToDeck = 1,
        };
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        if (!ok) _failed++;
    }
}
