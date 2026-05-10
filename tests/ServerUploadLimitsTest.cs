using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KivrioChat;

namespace KivrioChatTests
{
    internal static class Program
    {
        private static int Main()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-upload-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dataDir);
                var store = new DataStore(dataDir);
                Dictionary<string, object> conversation = store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Upload limits test" }
                });
                string conversationId = Convert.ToString(conversation["id"]);

                List<Dictionary<string, object>> stored = store.CreateAttachments(conversationId, new List<UploadedFile>
                {
                    UploadFile("note.txt", "text/plain", Encoding.UTF8.GetBytes("hello"))
                });
                Assert(stored.Count == 1, "valid text upload should be stored");
                string storedAttachmentId = Convert.ToString(stored[0]["id"]);
                string storedAttachmentPath = store.GetAttachmentPath(store.GetAttachment(storedAttachmentId));
                Assert(File.Exists(storedAttachmentPath), "valid text upload should exist on disk");

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("run.exe", "application/octet-stream", new byte[] { 1 })
                        });
                    },
                    400,
                    "unsupported extension should be rejected"
                );

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("image.png", "text/plain", new byte[] { 1 })
                        });
                    },
                    400,
                    "mismatched image MIME should be rejected"
                );

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("image.png", "image/png", new byte[(10 * 1024 * 1024) + 1])
                        });
                    },
                    413,
                    "oversized image should be rejected"
                );

                var sixFiles = new List<UploadedFile>();
                for (int i = 0; i < 6; i++)
                {
                    sixFiles.Add(UploadFile("note-" + i + ".txt", "text/plain", new byte[] { 1 }));
                }
                ExpectUploadValidation(
                    delegate { store.CreateAttachments(conversationId, sixFiles); },
                    413,
                    "more than five files should be rejected"
                );

                Assert(store.DeleteConversation(conversationId), "conversation delete should succeed");
                Assert(store.GetAttachment(storedAttachmentId) == null, "conversation delete should remove attachment record");
                Assert(!File.Exists(storedAttachmentPath), "conversation delete should remove attachment file");

                Dictionary<string, object> truncateConversation = store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Truncate cleanup test" }
                });
                string truncateConversationId = Convert.ToString(truncateConversation["id"]);
                List<Dictionary<string, object>> keptAttachments = store.CreateAttachments(truncateConversationId, new List<UploadedFile>
                {
                    UploadFile("kept.txt", "text/plain", Encoding.UTF8.GetBytes("keep"))
                });
                List<Dictionary<string, object>> removedAttachments = store.CreateAttachments(truncateConversationId, new List<UploadedFile>
                {
                    UploadFile("removed.txt", "text/plain", Encoding.UTF8.GetBytes("remove"))
                });
                string keptAttachmentId = Convert.ToString(keptAttachments[0]["id"]);
                string removedAttachmentId = Convert.ToString(removedAttachments[0]["id"]);
                string keptAttachmentPath = store.GetAttachmentPath(store.GetAttachment(keptAttachmentId));
                string removedAttachmentPath = store.GetAttachmentPath(store.GetAttachment(removedAttachmentId));

                Dictionary<string, object> keptMessage = store.AddMessage(truncateConversationId, new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", "original" },
                    { "attachment_ids", new object[] { keptAttachmentId } }
                });
                store.AddMessage(truncateConversationId, new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", "to remove" },
                    { "attachment_ids", new object[] { removedAttachmentId } }
                });

                store.UpdateMessage(truncateConversationId, Convert.ToString(keptMessage["id"]), new Dictionary<string, object>
                {
                    { "content", "edited" },
                    { "truncate_following", true }
                });

                Assert(store.GetAttachment(keptAttachmentId) != null, "truncate should keep edited message attachment record");
                Assert(File.Exists(keptAttachmentPath), "truncate should keep edited message attachment file");
                Assert(store.GetAttachment(removedAttachmentId) == null, "truncate should remove following message attachment record");
                Assert(!File.Exists(removedAttachmentPath), "truncate should remove following message attachment file");

                Console.WriteLine("server upload cleanup tests passed");
                return 0;
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static UploadedFile UploadFile(string name, string contentType, byte[] content)
        {
            return new UploadedFile
            {
                FileName = name,
                ContentType = contentType,
                Content = content
            };
        }

        private static void ExpectUploadValidation(Action action, int expectedStatus, string label)
        {
            try
            {
                action();
            }
            catch (UploadValidationException ex)
            {
                Assert((int)ex.StatusCode == expectedStatus, label + ": expected " + expectedStatus + ", got " + (int)ex.StatusCode);
                return;
            }

            throw new Exception(label + ": expected UploadValidationException");
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
