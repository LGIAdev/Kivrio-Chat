using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KivrioChat;

namespace KivrioChatPersistenceTests
{
    internal static class Program
    {
        private static int Main()
        {
            TestLegacyStoreGetsSchemaVersionAndMigrationBackup();
            TestCorruptPrimaryStoreRecoversFromBackup();

            Console.WriteLine("server persistence tests passed");
            return 0;
        }

        private static void TestLegacyStoreGetsSchemaVersionAndMigrationBackup()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-migration-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dataDir);
                string storePath = Path.Combine(dataDir, "kivrio-chat.json");
                File.WriteAllText(storePath, LegacyStoreJson("Legacy conversation"), Encoding.UTF8);

                var store = new DataStore(dataDir);
                List<Dictionary<string, object>> conversations = store.ListConversations();

                Assert(conversations.Count == 1, "legacy conversation should survive migration");
                Assert(Convert.ToString(conversations[0]["title"]) == "Legacy conversation", "legacy title should survive migration");

                string activeStore = File.ReadAllText(storePath, Encoding.UTF8);
                Assert(activeStore.Contains("\"schemaVersion\":1"), "migration should persist current schema version");
                Assert(activeStore.Contains("Legacy message"), "migration should preserve message content");

                string[] backups = Directory.GetFiles(dataDir, "kivrio-chat.json.pre-migration-v0-to-v1-*.bak");
                Assert(backups.Length == 1, "migration should keep one pre-migration backup");
                Assert(!File.ReadAllText(backups[0], Encoding.UTF8).Contains("schemaVersion"), "migration backup should preserve legacy source");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static void TestCorruptPrimaryStoreRecoversFromBackup()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-bak-recovery-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dataDir);
                string storePath = Path.Combine(dataDir, "kivrio-chat.json");
                File.WriteAllText(storePath, "{not-json", Encoding.UTF8);
                File.WriteAllText(storePath + ".bak", CurrentStoreJson("Recovered from backup"), Encoding.UTF8);

                var store = new DataStore(dataDir);
                List<Dictionary<string, object>> conversations = store.ListConversations();

                Assert(conversations.Count == 1, "valid backup should recover conversations when primary store is corrupt");
                Assert(Convert.ToString(conversations[0]["title"]) == "Recovered from backup", "backup recovery should use backup content");

                string activeStore = File.ReadAllText(storePath, Encoding.UTF8);
                Assert(activeStore.Contains("Recovered from backup"), "backup recovery should rewrite active store");
                Assert(Directory.GetFiles(dataDir, "kivrio-chat.json.corrupt-*.bak").Length == 1, "corrupt primary should be preserved");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static string LegacyStoreJson(string title)
        {
            return "{"
                + "\"systemPrompt\":\"\","
                + "\"systemPromptUpdatedAt\":0,"
                + "\"conversations\":[{"
                + "\"id\":\"c_legacy\","
                + "\"title\":\"" + title + "\","
                + "\"createdAt\":1,"
                + "\"updatedAt\":2,"
                + "\"archived\":0,"
                + "\"messages\":[{"
                + "\"id\":\"m_legacy\","
                + "\"conversationId\":\"c_legacy\","
                + "\"role\":\"user\","
                + "\"content\":\"Legacy message\","
                + "\"createdAt\":3"
                + "}]"
                + "}]"
                + "}";
        }

        private static string CurrentStoreJson(string title)
        {
            return "{"
                + "\"schemaVersion\":1,"
                + "\"systemPrompt\":\"\","
                + "\"systemPromptUpdatedAt\":0,"
                + "\"folders\":[],"
                + "\"attachments\":[],"
                + "\"conversations\":[{"
                + "\"id\":\"c_backup\","
                + "\"title\":\"" + title + "\","
                + "\"createdAt\":1,"
                + "\"updatedAt\":2,"
                + "\"archived\":0,"
                + "\"messages\":[]"
                + "}]"
                + "}";
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }
    }
}
