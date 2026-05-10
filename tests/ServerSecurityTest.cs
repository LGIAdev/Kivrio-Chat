using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using KivrioChat;

namespace KivrioChatSecurityTests
{
    internal static class Program
    {
        private static int Main()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-security-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dataDir);
                var store = new DataStore(dataDir);

                TestTraversalProtection(store);
                TestStaticTraversalRejected();
                TestMimeRejections(store);
                TestCrossConversationAttachmentAccess(store);
                TestAgentStatusEndpointRemoved();
                TestUnsafeCrossOriginRejected();
                TestAuthRateLimit();
                TestSecurityHeaders();
                TestStructuredLogsAvoidSensitiveFields();
                TestCorruptStoreBackedUpAndRecovered();
                TestLargeContentLength("/api/system-prompt", (4L * 1024L * 1024L) + 1);
                TestLargeContentLength("/api/conversations/c1/attachments", (30L * 1024L * 1024L) + 1);

                Console.WriteLine("server security tests passed");
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

        private static void TestTraversalProtection(DataStore store)
        {
            Assert(
                store.GetAttachmentPath(new AttachmentRecord { relativePath = Path.Combine("uploads", "..", "auth.json") }) == "",
                "attachment path traversal should be rejected"
            );
            Assert(
                store.GetAttachmentPath(new AttachmentRecord { relativePath = Path.Combine("uploads2", "fake.txt") }) == "",
                "attachment path prefix sibling should be rejected"
            );
        }

        private static void TestStaticTraversalRejected()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-static-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "js"));
                Directory.CreateDirectory(Path.Combine(root, "assets"));
                Directory.CreateDirectory(Path.Combine(root, "data"));
                File.WriteAllText(Path.Combine(root, "js", "app.js"), "console.log('ok');", Encoding.UTF8);
                File.WriteAllText(Path.Combine(root, "data", "auth.json"), "secret", Encoding.UTF8);

                var server = new LocalServer(root, "127.0.0.1", 0);
                HttpResponse publicFile = InvokeServeStatic(server, StaticRequest("/js/app.js"));
                Assert(publicFile.StatusCode == HttpStatusCode.OK, "public static file should be served");

                HttpResponse escapedFromJs = InvokeServeStatic(server, StaticRequest("/js/../data/auth.json"));
                Assert(escapedFromJs.StatusCode == HttpStatusCode.NotFound, "static traversal from js should be rejected");
                Assert(!ResponseText(escapedFromJs).Contains("secret"), "static traversal from js should not disclose file content");

                HttpResponse escapedFromAssets = InvokeServeStatic(server, StaticRequest("/assets/../data/auth.json"));
                Assert(escapedFromAssets.StatusCode == HttpStatusCode.NotFound, "static traversal from assets should be rejected");
                Assert(!ResponseText(escapedFromAssets).Contains("secret"), "static traversal from assets should not disclose file content");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestMimeRejections(DataStore store)
        {
            string conversationId = CreateConversationId(store, "MIME rejection test");

            ExpectUploadValidation(
                delegate
                {
                    store.CreateAttachments(conversationId, new List<UploadedFile>
                    {
                        UploadFile("document.pdf", "text/plain", new byte[] { 1 })
                    });
                },
                400,
                "PDF with text MIME should be rejected"
            );

            ExpectUploadValidation(
                delegate
                {
                    store.CreateAttachments(conversationId, new List<UploadedFile>
                    {
                        UploadFile("notes.txt", "image/png", new byte[] { 1 })
                    });
                },
                400,
                "text file with image MIME should be rejected"
            );
        }

        private static void TestCrossConversationAttachmentAccess(DataStore store)
        {
            string firstConversationId = CreateConversationId(store, "Attachment owner");
            string secondConversationId = CreateConversationId(store, "Attachment attacker");

            List<Dictionary<string, object>> attachments = store.CreateAttachments(firstConversationId, new List<UploadedFile>
            {
                UploadFile("owned.txt", "text/plain", Encoding.UTF8.GetBytes("owned"))
            });
            string attachmentId = Convert.ToString(attachments[0]["id"]);

            Dictionary<string, object> message = store.AddMessage(secondConversationId, new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", "try to attach another conversation file" },
                { "attachment_ids", new object[] { attachmentId } }
            });

            var serializedAttachments = message["attachments"] as List<Dictionary<string, object>>;
            Assert(serializedAttachments != null && serializedAttachments.Count == 0, "cross-conversation attachment should not serialize on message");
            Assert(store.GetAttachment(attachmentId).messageId == null, "cross-conversation attachment should not be linked");
        }

        private static void TestAgentStatusEndpointRemoved()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-agentless-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");

                var server = new LocalServer(root, "127.0.0.1", 0);
                var request = new HttpRequest
                {
                    Method = "GET",
                    Target = "/api/agent/status",
                    Path = "/api/agent/status"
                };
                HttpResponse response = InvokeRouteApi(server, request);

                Assert(response.StatusCode == HttpStatusCode.NotFound, "agent status endpoint should be removed");
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

        private static void TestUnsafeCrossOriginRejected()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-origin-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse rejected = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/conversations",
                    "127.0.0.1:8020",
                    "http://evil.example",
                    "{}"));
                Assert(rejected.StatusCode == HttpStatusCode.Forbidden, "cross-origin unsafe request should be rejected");

                HttpResponse allowed = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/conversations",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{}"));
                Assert(allowed.StatusCode == HttpStatusCode.Created, "same-origin unsafe request should be allowed");
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

        private static void TestAuthRateLimit()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-auth-rate-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousAdminPassword = Environment.GetEnvironmentVariable("KIVRO_ADMIN_PASSWORD");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "0");
                Environment.SetEnvironmentVariable("KIVRO_ADMIN_PASSWORD", "correct-password");

                var server = new LocalServer(root, "127.0.0.1", 8020);
                for (int i = 0; i < 4; i++)
                {
                    HttpResponse wrong = InvokeRouteApi(server, AuthRequest("wrong-password"));
                    Assert(wrong.StatusCode == HttpStatusCode.Unauthorized, "wrong login before limit should be unauthorized");
                }

                HttpResponse success = InvokeRouteApi(server, AuthRequest("correct-password"));
                Assert(success.StatusCode == HttpStatusCode.OK, "successful login before limit should be allowed");
                Assert(success.Headers.ContainsKey("Set-Cookie"), "successful login should set a session cookie");

                for (int i = 0; i < 4; i++)
                {
                    HttpResponse wrong = InvokeRouteApi(server, AuthRequest("wrong-password"));
                    Assert(wrong.StatusCode == HttpStatusCode.Unauthorized, "wrong login after reset should be unauthorized");
                }

                HttpResponse locked = InvokeRouteApi(server, AuthRequest("wrong-password"));
                Assert((int)locked.StatusCode == 429, "fifth failed login should be rate limited");
                Assert(locked.Headers.ContainsKey("Retry-After"), "rate limited login should include Retry-After");

                HttpResponse lockedCorrect = InvokeRouteApi(server, AuthRequest("correct-password"));
                Assert((int)lockedCorrect.StatusCode == 429, "correct login should stay locked during rate limit window");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRO_ADMIN_PASSWORD", previousAdminPassword);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestSecurityHeaders()
        {
            var response = new HttpResponse(HttpStatusCode.OK, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));

            Assert(response.Headers["X-Content-Type-Options"] == "nosniff", "security header should prevent MIME sniffing");
            Assert(response.Headers["X-Frame-Options"] == "DENY", "security header should prevent framing");
            Assert(response.Headers["Referrer-Policy"] == "no-referrer", "security header should suppress referrers");
            Assert(response.Headers["Cross-Origin-Opener-Policy"] == "same-origin", "security header should isolate opener context");
            Assert(response.Headers["Cross-Origin-Resource-Policy"] == "same-origin", "security header should limit cross-origin resource use");
            Assert(response.Headers["Permissions-Policy"].Contains("camera=()"), "security header should disable unused browser permissions");

            string csp = response.Headers["Content-Security-Policy"];
            Assert(csp.Contains("frame-ancestors 'none'"), "CSP should prevent framing");
            Assert(csp.Contains("base-uri 'self'"), "CSP should restrict base URI");
            Assert(csp.Contains("object-src 'none'"), "CSP should disable plugins");
            Assert(!csp.Contains("connect-src") && !csp.Contains("script-src"), "CSP should not restrict Ollama connections or existing scripts");
        }

        private static void TestStructuredLogsAvoidSensitiveFields()
        {
            string secret = "super-secret-password-from-body";
            var unexpected = new InvalidDataException(secret);

            Assert(ErrorResponses.StatusFor(unexpected) == HttpStatusCode.InternalServerError, "unexpected errors should stay server errors");
            Assert(ErrorResponses.MessageFor(unexpected) == "Erreur serveur interne.", "unexpected errors should not expose exception messages");
            Assert(ErrorResponses.ReasonFor(unexpected) == "unhandled_exception", "unexpected errors should use a stable log reason");

            var upload = new UploadValidationException(HttpStatusCode.BadRequest, "Type de fichier non pris en charge: safe.txt");
            Assert(ErrorResponses.StatusFor(upload) == HttpStatusCode.BadRequest, "upload validation status should be preserved");
            Assert(ErrorResponses.MessageFor(upload).Contains("safe.txt"), "upload validation public message should be preserved");

            string logLine = ServerLog.FormatLine("error", "request_error", new Dictionary<string, object>
            {
                { "app", "kivrio-chat" },
                { "method", "POST" },
                { "path", "/api/auth/login" },
                { "status", 500 },
                { "exception", unexpected.GetType().Name },
                { "reason", ErrorResponses.ReasonFor(unexpected) },
                { "message", secret },
                { "cookie", "kivro_session=" + secret },
                { "authorization", "Bearer " + secret }
            });

            Assert(logLine.Contains("\"level\":\"error\""), "structured log should include level");
            Assert(logLine.Contains("\"event\":\"request_error\""), "structured log should include event");
            Assert(logLine.Contains("\"reason\":\"unhandled_exception\""), "structured log should include stable reason");
            Assert(!logLine.Contains(secret), "structured log should not include sensitive field values");
            Assert(!logLine.Contains("cookie"), "structured log should not include cookie fields");
            Assert(!logLine.Contains("authorization"), "structured log should not include authorization fields");
            Assert(!logLine.Contains("message"), "structured log should not include exception message fields");

            string startupLine = ServerLog.FormatLine("info", "server_start", new Dictionary<string, object>
            {
                { "app", "kivrio-chat" },
                { "root", @"C:\Users\gille\Documents\Kivrio Chat" },
                { "root_name", "Kivrio Chat" }
            });
            Assert(!startupLine.Contains(@"C:\Users"), "startup log should not include full local root paths");
            Assert(startupLine.Contains("\"root_name\":\"Kivrio Chat\""), "startup log should keep a non-sensitive root label");
        }

        private static void TestCorruptStoreBackedUpAndRecovered()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "kivrio-chat-corrupt-store-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dataDir);
                string storePath = Path.Combine(dataDir, "kivrio-chat.json");
                File.WriteAllText(storePath, "{not-json", Encoding.UTF8);

                var store = new DataStore(dataDir);
                Assert(store.ListConversations().Count == 0, "corrupt store should recover to an empty state");
                string[] corruptBackups = Directory.GetFiles(dataDir, "kivrio-chat.json.corrupt-*.bak");
                Assert(corruptBackups.Length == 1, "corrupt store should be moved to a backup file");
                Assert(File.ReadAllText(corruptBackups[0], Encoding.UTF8).Contains("{not-json"), "corrupt backup should preserve original content");
                Assert(!File.Exists(storePath), "corrupt store file should not remain active");

                store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Recovered conversation" }
                });
                Assert(File.Exists(storePath), "new store should be written after recovery");
                Assert(File.ReadAllText(storePath, Encoding.UTF8).Contains("Recovered conversation"), "new store should contain recovered data");

                store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Backup trigger" }
                });
                string backupPath = storePath + ".bak";
                Assert(File.Exists(backupPath), "atomic rewrite should keep a previous-version backup");
                Assert(Directory.GetFiles(dataDir, "*.tmp").Length == 0, "successful atomic writes should not leave temp files");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, true);
                }
            }
        }

        private static void TestLargeContentLength(string path, long contentLength)
        {
            Exception serverException = null;
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Thread thread = new Thread(delegate()
            {
                try
                {
                    using (TcpClient serverClient = listener.AcceptTcpClient())
                    using (NetworkStream stream = serverClient.GetStream())
                    {
                        HttpRequest.Read(stream);
                    }
                }
                catch (Exception ex)
                {
                    serverException = ex;
                }
                finally
                {
                    listener.Stop();
                }
            });
            thread.IsBackground = true;
            thread.Start();

            using (TcpClient client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                string request = "POST " + path + " HTTP/1.1\r\n"
                    + "Host: 127.0.0.1\r\n"
                    + "Content-Length: " + contentLength + "\r\n"
                    + "\r\n";
                byte[] bytes = Encoding.ASCII.GetBytes(request);
                client.GetStream().Write(bytes, 0, bytes.Length);
            }

            thread.Join(5000);
            Assert(serverException is RequestBodyTooLargeException, "large Content-Length should be rejected for " + path);
        }

        private static string CreateConversationId(DataStore store, string title)
        {
            Dictionary<string, object> conversation = store.CreateConversation(new Dictionary<string, object>
            {
                { "title", title }
            });
            return Convert.ToString(conversation["id"]);
        }

        private static HttpRequest JsonRequest(string method, string path, string host, string origin, string body)
        {
            var request = new HttpRequest
            {
                Method = method,
                Target = path,
                Path = path,
                Body = Encoding.UTF8.GetBytes(body ?? "")
            };
            request.Headers["Host"] = host;
            request.Headers["Origin"] = origin;
            request.Headers["Content-Type"] = "application/json";
            return request;
        }

        private static HttpRequest AuthRequest(string password)
        {
            return JsonRequest(
                "POST",
                "/api/auth/login",
                "127.0.0.1:8020",
                "http://127.0.0.1:8020",
                "{\"password\":\"" + password + "\"}");
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

        private static HttpResponse InvokeServeStatic(LocalServer server, HttpRequest request)
        {
            MethodInfo serveStatic = typeof(LocalServer).GetMethod("ServeStatic", BindingFlags.Instance | BindingFlags.NonPublic);
            return (HttpResponse)serveStatic.Invoke(server, new object[] { request });
        }

        private static string ResponseText(HttpResponse response)
        {
            return Encoding.UTF8.GetString(response.Body ?? new byte[0]);
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
