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
            TestWebSourcesRoundTrip();
            TestStaleRecoveryBackupIsSanitizedOnLoad();
            TestDeletedConversationDoesNotSurviveRecoveryBackup();
            TestDeletedFolderDoesNotSurviveRecoveryBackup();

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

        private static string EmptyCurrentStoreJson()
        {
            return "{"
                + "\"schemaVersion\":1,"
                + "\"systemPrompt\":\"\","
                + "\"systemPromptUpdatedAt\":0,"
                + "\"folders\":[],"
                + "\"attachments\":[],"
                + "\"conversations\":[]"
                + "}";
        }

        private static void TestWebSourcesRoundTrip()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-web-sources-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new DataStore(dataDir);
                Dictionary<string, object> conversation = store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Sources Web" }
                });
                string conversationId = Convert.ToString(conversation["id"]);

                Dictionary<string, object> message = store.AddMessage(conversationId, new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", "Reponse citee" },
                    { "web_sources", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "index", 99 },
                                { "title", "Source <b>A</b>" },
                                { "url", "https://example.test/a" },
                                { "snippet", "Extrait <em>A</em>" },
                                { "source", "searxng" }
                            },
                            new Dictionary<string, object>
                            {
                                { "title", "URL invalide" },
                                { "url", "javascript:alert(1)" }
                            }
                        }
                    }
                });

                var sources = message["webSources"] as List<Dictionary<string, object>>;
                Assert(sources != null && sources.Count == 1, "web sources should be serialized and invalid URLs rejected");
                Assert(Convert.ToInt32(sources[0]["index"]) == 1, "web source index should be normalized");
                Assert(Convert.ToString(sources[0]["title"]) == "Source A", "web source title should be cleaned");
                Assert(Convert.ToString(sources[0]["snippet"]) == "Extrait A", "web source snippet should be cleaned");

                var reloaded = new DataStore(dataDir);
                List<Dictionary<string, object>> messages = reloaded.GetConversationMessages(conversationId);
                var reloadedSources = messages[0]["webSources"] as List<Dictionary<string, object>>;
                Assert(reloadedSources != null && reloadedSources.Count == 1, "web sources should survive store reload");
                Assert(Convert.ToString(reloadedSources[0]["url"]) == "https://example.test/a", "web source URL should survive reload");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static void TestDeletedConversationDoesNotSurviveRecoveryBackup()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-delete-backup-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string marker = "deleted-conversation-marker-" + Guid.NewGuid().ToString("N");
                string storePath = Path.Combine(dataDir, "kivrio-chat.json");
                var store = new DataStore(dataDir);
                Dictionary<string, object> conversation = store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Conversation to delete " + marker }
                });
                string conversationId = Convert.ToString(conversation["id"]);

                store.AddMessage(conversationId, new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", "Answer with source " + marker },
                    { "web_sources", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "title", "Deleted source " + marker },
                                { "url", "https://example.test/deleted-" + marker },
                                { "snippet", "Deleted snippet " + marker },
                                { "source", "searxng" }
                            }
                        }
                    }
                });

                Assert(File.ReadAllText(storePath, Encoding.UTF8).Contains(marker), "test setup should persist conversation before delete");
                Assert(store.DeleteConversation(conversationId), "conversation delete should succeed");

                string activeStore = File.ReadAllText(storePath, Encoding.UTF8);
                string recoveryBackup = File.ReadAllText(storePath + ".bak", Encoding.UTF8);
                Assert(!activeStore.Contains(marker), "deleted conversation should be absent from active store");
                Assert(!recoveryBackup.Contains(marker), "deleted conversation should be absent from recovery backup");

                File.WriteAllText(storePath, "{not-json", Encoding.UTF8);
                var recovered = new DataStore(dataDir);
                Assert(recovered.ListConversations().Count == 0, "deleted conversation should not be recovered from backup");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static void TestStaleRecoveryBackupIsSanitizedOnLoad()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-stale-backup-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dataDir);
                string marker = "stale-deleted-conversation-" + Guid.NewGuid().ToString("N");
                string storePath = Path.Combine(dataDir, "kivrio-chat.json");
                File.WriteAllText(storePath, EmptyCurrentStoreJson(), Encoding.UTF8);
                File.WriteAllText(storePath + ".bak", CurrentStoreJson(marker), Encoding.UTF8);

                var store = new DataStore(dataDir);
                Assert(store.ListConversations().Count == 0, "active store should remain authoritative when it is valid");
                Assert(!File.ReadAllText(storePath + ".bak", Encoding.UTF8).Contains(marker), "stale backup should be sanitized from active store on load");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static void TestDeletedFolderDoesNotSurviveRecoveryBackup()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-delete-folder-backup-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string marker = "deleted-folder-marker-" + Guid.NewGuid().ToString("N");
                string storePath = Path.Combine(dataDir, "kivrio-chat.json");
                var store = new DataStore(dataDir);
                Dictionary<string, object> folder = store.CreateFolder(new Dictionary<string, object>
                {
                    { "name", "Test " + marker }
                });
                string folderId = Convert.ToString(folder["id"]);

                Assert(File.ReadAllText(storePath, Encoding.UTF8).Contains(marker), "test setup should persist folder before delete");
                Assert(store.DeleteFolder(folderId), "folder delete should succeed");

                string activeStore = File.ReadAllText(storePath, Encoding.UTF8);
                string recoveryBackup = File.ReadAllText(storePath + ".bak", Encoding.UTF8);
                Assert(!activeStore.Contains(marker), "deleted folder should be absent from active store");
                Assert(!recoveryBackup.Contains(marker), "deleted folder should be absent from recovery backup");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
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
