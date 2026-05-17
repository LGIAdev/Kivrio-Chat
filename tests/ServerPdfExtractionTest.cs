using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using KivrioChat;

namespace KivrioChatPdfTests
{
    internal static class Program
    {
        private static int Main()
        {
            string repoRoot = FindRepoRoot();
            LocalDependencyResolver.Register(repoRoot);

            TestPdfTextExtractor();
            TestPdfTextEndpoint();

            Console.WriteLine("server PDF extraction tests passed");
            return 0;
        }

        private static void TestPdfTextExtractor()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-pdf-extractor-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string pdfPath = Path.Combine(root, "sample.pdf");
                File.WriteAllBytes(pdfPath, CreateSimplePdf("Hello Kivrio PDF"));

                PdfTextExtractionResult result = PdfTextExtractor.ExtractText(pdfPath, 10000);
                Assert(result.PageCount == 1, "PDF extractor should report one page");
                Assert(!result.Truncated, "short PDF extraction should not be truncated");
                Assert(result.Text.Contains("Hello Kivrio PDF"), "PDF extractor should read text content");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestPdfTextEndpoint()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-pdf-route-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");

                byte[] pdfBytes = CreateSimplePdf("Endpoint PDF content");
                var server = new LocalServer(root, "127.0.0.1", 8020);
                DataStore store = GetPrivateStore(server);
                string conversationId = CreateConversationId(store, "PDF route test");

                List<Dictionary<string, object>> pdfAttachments = store.CreateAttachments(conversationId, new List<UploadedFile>
                {
                    UploadFile("document.pdf", "application/pdf", pdfBytes)
                });
                string pdfAttachmentId = Convert.ToString(pdfAttachments[0]["id"]);
                Assert(Convert.ToBoolean(pdfAttachments[0]["isPdf"]), "serialized PDF attachment should be marked as PDF");
                Assert(Convert.ToString(pdfAttachments[0]["textUrl"]).EndsWith("/text", StringComparison.Ordinal), "serialized PDF attachment should expose text URL");

                HttpResponse textResponse = InvokeRouteApi(server, StaticRequest("/api/attachments/" + pdfAttachmentId + "/text"));
                Assert(textResponse.StatusCode == HttpStatusCode.OK, "PDF text endpoint should return OK");
                Dictionary<string, object> payload = JsonObject(textResponse);
                Assert(Convert.ToBoolean(payload["ok"]), "PDF text endpoint should report ok");
                Assert(Convert.ToInt32(payload["pageCount"]) == 1, "PDF text endpoint should report page count");
                Assert(Convert.ToString(payload["text"]).Contains("Endpoint PDF content"), "PDF text endpoint should return extracted text");
                Assert(!Convert.ToBoolean(payload["truncated"]), "short PDF endpoint response should not be truncated");

                HttpResponse contentResponse = InvokeRouteApi(server, StaticRequest("/api/attachments/" + pdfAttachmentId + "/content"));
                Assert(contentResponse.StatusCode == HttpStatusCode.OK, "PDF content endpoint should return OK");
                Assert(contentResponse.ContentType == "application/pdf", "PDF content endpoint should use application/pdf");
                Assert(contentResponse.Headers.ContainsKey("Content-Disposition"), "PDF content endpoint should force attachment download");
                Assert(contentResponse.Body.Length == pdfBytes.Length, "PDF content endpoint should return original bytes");

                List<Dictionary<string, object>> textAttachments = store.CreateAttachments(conversationId, new List<UploadedFile>
                {
                    UploadFile("notes.txt", "text/plain", Encoding.UTF8.GetBytes("not a PDF"))
                });
                string textAttachmentId = Convert.ToString(textAttachments[0]["id"]);
                HttpResponse invalidTextResponse = InvokeRouteApi(server, StaticRequest("/api/attachments/" + textAttachmentId + "/text"));
                Assert(invalidTextResponse.StatusCode == HttpStatusCode.BadRequest, "text extraction endpoint should reject non-PDF attachments");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static byte[] CreateSimplePdf(string text)
        {
            var offsets = new List<int>();
            var builder = new StringBuilder();
            builder.Append("%PDF-1.4\n");
            AppendObject(builder, offsets, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            AppendObject(builder, offsets, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            AppendObject(builder, offsets, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
            AppendObject(builder, offsets, "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

            string stream = "BT\n/F1 24 Tf\n72 720 Td\n(" + EscapePdfLiteral(text) + ") Tj\nET\n";
            string streamObject = "5 0 obj\n<< /Length " + Encoding.ASCII.GetByteCount(stream) + " >>\nstream\n" + stream + "endstream\nendobj\n";
            AppendObject(builder, offsets, streamObject);

            int xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append("xref\n0 6\n");
            builder.Append("0000000000 65535 f \n");
            foreach (int offset in offsets)
            {
                builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
            }
            builder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
            builder.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        private static void AppendObject(StringBuilder builder, List<int> offsets, string value)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(value);
        }

        private static string EscapePdfLiteral(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        }

        private static string FindRepoRoot()
        {
            string current = Environment.CurrentDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
            {
                if (File.Exists(Path.Combine(current, "server", "KivrioChatServer.cs")))
                {
                    return current;
                }

                DirectoryInfo parent = Directory.GetParent(current);
                current = parent == null ? null : parent.FullName;
            }
            return Environment.CurrentDirectory;
        }

        private static string CreateConversationId(DataStore store, string title)
        {
            Dictionary<string, object> conversation = store.CreateConversation(new Dictionary<string, object>
            {
                { "title", title }
            });
            return Convert.ToString(conversation["id"]);
        }

        private static HttpRequest StaticRequest(string path)
        {
            return new HttpRequest
            {
                Method = "GET",
                Target = path,
                Path = path
            };
        }

        private static HttpResponse InvokeRouteApi(LocalServer server, HttpRequest request)
        {
            MethodInfo routeApi = typeof(LocalServer).GetMethod("RouteApi", BindingFlags.Instance | BindingFlags.NonPublic);
            return (HttpResponse)routeApi.Invoke(server, new object[] { request });
        }

        private static DataStore GetPrivateStore(LocalServer server)
        {
            FieldInfo field = typeof(LocalServer).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "server store field should exist");
            return (DataStore)field.GetValue(server);
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

        private static Dictionary<string, object> JsonObject(HttpResponse response)
        {
            object parsed = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(response.Body ?? new byte[0]));
            Dictionary<string, object> payload = parsed as Dictionary<string, object>;
            Assert(payload != null, "response should be a JSON object");
            return payload;
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
