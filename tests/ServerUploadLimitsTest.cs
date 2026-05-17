using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
                Assert(File.ReadAllText(storedAttachmentPath, Encoding.UTF8) == "hello", "valid text upload should preserve exact bytes");
                Assert(Directory.GetFiles(dataDir, "*.tmp", SearchOption.AllDirectories).Length == 0, "successful attachment upload should not leave temp files");

                List<Dictionary<string, object>> storedSafeName = store.CreateAttachments(conversationId, new List<UploadedFile>
                {
                    UploadFile(@"..\nested\evil.txt", "text/plain", Encoding.UTF8.GetBytes("safe name"))
                });
                Assert(Convert.ToString(storedSafeName[0]["filename"]) == "evil.txt", "upload filename should be reduced to a safe basename");

                List<Dictionary<string, object>> storedImage = store.CreateAttachments(conversationId, new List<UploadedFile>
                {
                    UploadFile("pixel.png", "image/png", MinimalPng())
                });
                Assert(storedImage.Count == 1, "valid PNG upload should be stored");

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
                            UploadFile("image.png", "image/png", Encoding.UTF8.GetBytes("<html>not an image</html>"))
                        });
                    },
                    400,
                    "image extension with HTML content should be rejected"
                );

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("document.pdf", "application/pdf", Encoding.UTF8.GetBytes("not a pdf"))
                        });
                    },
                    400,
                    "PDF extension without PDF signature should be rejected"
                );

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("notes.txt", "text/plain", new byte[] { 0x00, 0x01, 0x02 })
                        });
                    },
                    400,
                    "text attachment with binary control bytes should be rejected"
                );

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("notes.txt", "text/plain", Encoding.UTF8.GetBytes("<script>alert(1)</script>"))
                        });
                    },
                    400,
                    "text attachment with active HTML should be rejected"
                );

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("notes.txt", "text/html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"))
                        });
                    },
                    400,
                    "text attachment with HTML MIME should be rejected"
                );

                ExpectUploadValidation(
                    delegate
                    {
                        store.CreateAttachments(conversationId, new List<UploadedFile>
                        {
                            UploadFile("notes.md", "text/html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"))
                        });
                    },
                    400,
                    "markdown attachment with HTML MIME should be rejected"
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
                    sixFiles.Add(UploadFile("note-" + i + ".txt", "text/plain", Encoding.UTF8.GetBytes("note " + i)));
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

                TestTransactionalUploadRollback();

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

        private static byte[] MinimalPng()
        {
            return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
        }

        private static void TestTransactionalUploadRollback()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-upload-rollback-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dataDir);
                var store = new DataStore(dataDir);
                Dictionary<string, object> conversation = store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Rollback test" }
                });
                string conversationId = Convert.ToString(conversation["id"]);
                SetPrivateField(store, "_storePath", dataDir + Path.DirectorySeparatorChar + "bad" + '\0' + "store.json");

                bool failed = false;
                try
                {
                    store.CreateAttachments(conversationId, new List<UploadedFile>
                    {
                        UploadFile("rollback.txt", "text/plain", Encoding.UTF8.GetBytes("rollback"))
                    });
                }
                catch (Exception ex)
                {
                    failed = !(ex is UploadValidationException);
                }

                Assert(failed, "upload should fail when persistence fails");
                Assert(AttachmentCount(store) == 0, "failed upload should roll back attachment records");

                string uploadsDir = Path.Combine(dataDir, "uploads");
                int storedFiles = Directory.Exists(uploadsDir)
                    ? Directory.GetFiles(uploadsDir, "*", SearchOption.AllDirectories).Length
                    : 0;
                Assert(storedFiles == 0, "failed upload should remove files written before persistence failure");
                int tempFiles = Directory.Exists(uploadsDir)
                    ? Directory.GetFiles(uploadsDir, "*.tmp", SearchOption.AllDirectories).Length
                    : 0;
                Assert(tempFiles == 0, "failed upload should remove temporary attachment files");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "private field should exist: " + fieldName);
            field.SetValue(target, value);
        }

        private static int AttachmentCount(DataStore store)
        {
            FieldInfo dataField = typeof(DataStore).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(dataField != null, "data field should exist");
            object data = dataField.GetValue(store);
            PropertyInfo attachmentsProperty = data.GetType().GetProperty("attachments", BindingFlags.Instance | BindingFlags.Public);
            Assert(attachmentsProperty != null, "attachments property should exist");
            ICollection attachments = attachmentsProperty.GetValue(data, null) as ICollection;
            return attachments == null ? 0 : attachments.Count;
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
