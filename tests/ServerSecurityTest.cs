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
                TestWebSearchUnavailableContract();
                TestWebSearchRejectsInvalidQuery();
                TestWebSearchLocalSearxngContract();
                TestWebSearchRejectsNonLocalBaseUrl();
                TestSearxngLauncherPathResolution();
                TestManagedWebSearchRuntimePurge();
                TestConversationDeletePurgesManagedWebSearchRuntime();
                TestSearxngLauncherOutputValidation();
                TestWebSearchManagedLauncherEnabledByDefault();
                TestWebSearchManagedLauncherCommandContract();
                TestWebSearchMockLauncherDisabledByDefault();
                TestWebSearchMockLauncherRejectsNonLocalBaseUrl();
                TestWebSearchMockLauncherFullChain();
                TestUnsafeCrossOriginRejected();
                TestShutdownEndpointSecurityAndState();
                TestAuthRateLimit();
                TestSecurityHeaders();
                TestStructuredLogsAvoidSensitiveFields();
                TestCorruptStoreBackedUpAndRecovered();
                TestLargeContentLength("/api/system-prompt", (4L * 1024L * 1024L) + 1);
                TestLargeContentLength("/api/web-search", (16L * 1024L) + 1);
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

        private static void TestShutdownEndpointSecurityAndState()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-shutdown-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousAdminPassword = Environment.GetEnvironmentVariable("KIVRO_ADMIN_PASSWORD");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "0");
                Environment.SetEnvironmentVariable("KIVRO_ADMIN_PASSWORD", "correct-password");

                var server = new LocalServer(root, "127.0.0.1", 8020);

                HttpRequest unauthenticated = JsonRequest(
                    "POST",
                    "/api/shutdown",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{}");
                unauthenticated.RemoteAddress = "127.0.0.1";
                HttpResponse unauthenticatedResponse = InvokeRouteApi(server, unauthenticated);
                Assert(unauthenticatedResponse.StatusCode == HttpStatusCode.Unauthorized, "shutdown should require an authenticated session");

                HttpRequest remote = JsonRequest(
                    "POST",
                    "/api/shutdown",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{}");
                remote.RemoteAddress = "192.0.2.10";
                HttpResponse remoteResponse = InvokeRouteApi(server, remote);
                Assert(remoteResponse.StatusCode == HttpStatusCode.Forbidden, "shutdown should reject non-loopback clients");

                HttpResponse login = InvokeRouteApi(server, AuthRequest("correct-password"));
                string sessionCookie = ExtractSessionCookie(login);

                HttpRequest shutdown = JsonRequest(
                    "POST",
                    "/api/shutdown",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{}");
                shutdown.RemoteAddress = "127.0.0.1";
                shutdown.Headers["Cookie"] = sessionCookie;
                HttpResponse response = InvokeRouteApi(server, shutdown);
                string body = ResponseText(response);

                Assert(response.StatusCode == HttpStatusCode.OK, "authenticated local shutdown should be accepted");
                Assert(body.Contains("\"shuttingDown\":true"), "shutdown response should report shutdown state");
                Assert(response.Headers.ContainsKey("Set-Cookie") && response.Headers["Set-Cookie"].Contains("Max-Age=0"), "shutdown should clear the session cookie");
                Assert(GetPrivateBool(server, "_shutdownRequested"), "shutdown should request the server run loop to stop");
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

        private static void TestWebSearchUnavailableContract()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-web-search-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", null);

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse response = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"message courant seulement\"}"));
                string body = ResponseText(response);

                Assert(response.StatusCode == HttpStatusCode.OK, "web search phase 2 contract should return OK");
                Assert(body.Contains("\"ok\":false"), "web search should not report success while unavailable");
                Assert(body.Contains("\"available\":false"), "web search should report unavailable");
                Assert(body.Contains("\"results\":[]"), "web search should return an empty result list");
                Assert(body.Contains("La recherche Web est momentan"), "web search should return the expected user message");
                Assert(!body.Contains("message courant seulement"), "web search should not echo the query");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", previousBaseUrl);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestWebSearchRejectsInvalidQuery()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-web-search-invalid-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", null);

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse empty = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"\"}"));
                Assert(empty.StatusCode == HttpStatusCode.BadRequest, "empty web search query should be rejected");

                string tooLong = new string('a', 401);
                HttpResponse longQuery = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"" + tooLong + "\"}"));
                Assert(longQuery.StatusCode == HttpStatusCode.BadRequest, "long web search query should be rejected");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", previousBaseUrl);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestWebSearchLocalSearxngContract()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-web-search-local-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL");
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            string receivedRequest = "";
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Thread serverThread = new Thread(delegate()
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        receivedRequest = ReadHttpHeader(stream);
                        byte[] body = Encoding.UTF8.GetBytes(
                            "{\"results\":["
                            + "{\"title\":\"<b>Result A</b>\",\"url\":\"https://example.com/a\",\"content\":\"One <em>snippet</em>\",\"engine\":\"demo\"},"
                            + "{\"title\":\"Duplicate\",\"url\":\"https://example.com/a\",\"content\":\"dup\",\"engine\":\"demo\"},"
                            + "{\"title\":\"Result B\",\"url\":\"https://example.org/b\",\"snippet\":\"Second snippet\",\"source\":\"example.org\"}"
                            + "]}"
                        );
                        byte[] head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n"
                            + "Content-Type: application/json\r\n"
                            + "Content-Length: " + body.Length + "\r\n"
                            + "Connection: close\r\n\r\n"
                        );
                        stream.Write(head, 0, head.Length);
                        stream.Write(body, 0, body.Length);
                    }
                });
                serverThread.IsBackground = true;
                serverThread.Start();

                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", "http://127.0.0.1:" + port + "/");
                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse response = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"message courant\",\"max_results\":5}"));
                serverThread.Join(5000);
                string bodyText = ResponseText(response);

                Assert(response.StatusCode == HttpStatusCode.OK, "local web search should return OK");
                Assert(receivedRequest.Contains("GET /search?"), "local web search should call the SearXNG search path");
                Assert(receivedRequest.Contains("q=message%20courant"), "local web search should send only the current query");
                Assert(bodyText.Contains("\"ok\":true"), "local web search should report success");
                Assert(bodyText.Contains("\"available\":true"), "local web search should report availability");
                Assert(bodyText.Contains("\"title\":\"Result A\""), "local web search should strip HTML from titles");
                Assert(bodyText.Contains("\"snippet\":\"One snippet\""), "local web search should strip HTML from snippets");
                Assert(bodyText.Contains("\"source\":\"demo\""), "local web search should keep source metadata");
                Assert(!bodyText.Contains("Duplicate"), "local web search should deduplicate URLs");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", previousBaseUrl);
                try { listener.Stop(); } catch { }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestWebSearchRejectsNonLocalBaseUrl()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-web-search-nonlocal-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", "http://example.com");

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse response = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"message courant\"}"));
                string body = ResponseText(response);

                Assert(response.StatusCode == HttpStatusCode.OK, "non-local web search base URL should fail softly");
                Assert(body.Contains("\"available\":false"), "non-local web search base URL should be rejected");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", previousBaseUrl);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestSearxngLauncherPathResolution()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-searxng-path-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string pythonDir = Path.Combine(root, "runtime", "python");
                string launcherDir = Path.Combine(root, "integrations", "searxng", "launcher");
                Directory.CreateDirectory(pythonDir);
                Directory.CreateDirectory(launcherDir);
                string pythonPath = Path.Combine(pythonDir, "python.exe");
                string launcherPath = Path.Combine(launcherDir, "start_searxng.py");
                string stopPath = Path.Combine(launcherDir, "stop_searxng.py");
                File.WriteAllText(pythonPath, "", Encoding.UTF8);
                File.WriteAllText(launcherPath, "", Encoding.UTF8);
                File.WriteAllText(stopPath, "", Encoding.UTF8);

                string resolvedPython;
                string resolvedLauncher;
                string resolvedStop;
                Assert(LocalServer.TryResolveEmbeddedPythonPath(root, out resolvedPython), "embedded Python path should resolve when the file exists");
                Assert(LocalServer.TryResolveSearxngStartScriptPath(root, out resolvedLauncher), "SearXNG start script should resolve when the file exists");
                Assert(LocalServer.TryResolveSearxngStopScriptPath(root, out resolvedStop), "SearXNG stop script should resolve when the file exists");
                Assert(Path.GetFullPath(resolvedPython) == Path.GetFullPath(pythonPath), "embedded Python should resolve inside runtime/python");
                Assert(Path.GetFullPath(resolvedLauncher) == Path.GetFullPath(launcherPath), "launcher should resolve inside integrations/searxng");
                Assert(Path.GetFullPath(resolvedStop) == Path.GetFullPath(stopPath), "stop script should resolve inside integrations/searxng");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestManagedWebSearchRuntimePurge()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-searxng-purge-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string runtime = Path.Combine(root, "integrations", "searxng", "runtime");
                string vendor = Path.Combine(root, "integrations", "searxng", "vendor");
                Directory.CreateDirectory(Path.Combine(runtime, "cache", "nested"));
                Directory.CreateDirectory(Path.Combine(runtime, "logs"));
                Directory.CreateDirectory(Path.Combine(runtime, "tmp"));
                Directory.CreateDirectory(vendor);
                File.WriteAllText(Path.Combine(runtime, "searxng.pid"), "123", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "searxng.stdout.log"), "query text", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "searxng.stderr.log"), "https://example.test/query", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "settings-launch.yml"), "secret", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "cache", "nested", "cache.txt"), "cached query", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "logs", "searx.log"), "logged query", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "tmp", "tmp.txt"), "tmp query", Encoding.UTF8);
                File.WriteAllText(Path.Combine(vendor, "keep.txt"), "vendor", Encoding.UTF8);

                LocalServer.PurgeManagedWebSearchRuntime(root);

                Assert(!File.Exists(Path.Combine(runtime, "searxng.pid")), "runtime purge should remove the PID file");
                Assert(!File.Exists(Path.Combine(runtime, "searxng.stdout.log")), "runtime purge should remove stdout logs");
                Assert(!File.Exists(Path.Combine(runtime, "searxng.stderr.log")), "runtime purge should remove stderr logs");
                Assert(!File.Exists(Path.Combine(runtime, "settings-launch.yml")), "runtime purge should remove launch settings");
                Assert(Directory.GetFileSystemEntries(Path.Combine(runtime, "cache")).Length == 0, "runtime purge should clear cache");
                Assert(Directory.GetFileSystemEntries(Path.Combine(runtime, "logs")).Length == 0, "runtime purge should clear logs");
                Assert(Directory.GetFileSystemEntries(Path.Combine(runtime, "tmp")).Length == 0, "runtime purge should clear tmp");
                Assert(File.Exists(Path.Combine(vendor, "keep.txt")), "runtime purge should not touch vendor files");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestConversationDeletePurgesManagedWebSearchRuntime()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-delete-purge-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            try
            {
                CreateFakeManagedSearxngRoot(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");

                string dataDir = Path.Combine(root, "data");
                var store = new DataStore(dataDir);
                Dictionary<string, object> conversation = store.CreateConversation(new Dictionary<string, object>
                {
                    { "title", "Conversation Web" }
                });
                string conversationId = Convert.ToString(conversation["id"]);

                string runtime = Path.Combine(root, "integrations", "searxng", "runtime");
                Directory.CreateDirectory(Path.Combine(runtime, "cache"));
                File.WriteAllText(Path.Combine(runtime, "searxng.pid"), "999999", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "searxng.stderr.log"), "query=https://example.test", Encoding.UTF8);
                File.WriteAllText(Path.Combine(runtime, "cache", "cache.txt"), "cached query", Encoding.UTF8);

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse response = InvokeRouteApi(server, JsonRequest(
                    "DELETE",
                    "/api/conversations/" + conversationId,
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{}"));

                Assert(response.StatusCode == HttpStatusCode.OK, "conversation delete should return OK");
                Assert(!File.Exists(Path.Combine(runtime, "searxng.pid")), "conversation delete should remove managed SearXNG PID");
                Assert(!File.Exists(Path.Combine(runtime, "searxng.stderr.log")), "conversation delete should remove managed SearXNG logs");
                Assert(Directory.GetFileSystemEntries(Path.Combine(runtime, "cache")).Length == 0, "conversation delete should clear managed SearXNG cache");
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

        private static void TestSearxngLauncherOutputValidation()
        {
            Uri baseUri;
            Assert(
                LocalServer.TryReadLauncherBaseUri("{\"ok\":true,\"base_url\":\"http://127.0.0.1:8030/\"}", out baseUri),
                "launcher output should accept loopback base URLs"
            );
            Assert(baseUri.ToString() == "http://127.0.0.1:8030/", "launcher output should preserve the local base URL");
            Assert(
                !LocalServer.TryReadLauncherBaseUri("{\"ok\":true,\"base_url\":\"http://example.com/\"}", out baseUri),
                "launcher output should reject non-local base URLs"
            );
            Assert(
                !LocalServer.TryReadLauncherBaseUri("{\"ok\":false,\"base_url\":\"http://127.0.0.1:8030/\"}", out baseUri),
                "launcher output should reject failed launcher responses"
            );
        }

        private static void TestWebSearchManagedLauncherEnabledByDefault()
        {
            string previousEnableManaged = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED");
            try
            {
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", null);

                Assert(LocalServer.IsManagedWebSearchEnabled(), "managed SearXNG real start should be enabled by default when the bundled runtime is present");
                Assert(
                    LocalServer.BuildSearxngLauncherArguments("start_searxng.py", true).Contains("--real-start"),
                    "managed launcher command should request a real start during normal Web Search"
                );

                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", "0");
                Assert(!LocalServer.IsManagedWebSearchEnabled(), "managed SearXNG real start should remain explicitly disableable");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", previousEnableManaged);
            }
        }

        private static void TestWebSearchManagedLauncherCommandContract()
        {
            string previousEnableManaged = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED");
            try
            {
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", null);

                Assert(LocalServer.IsManagedWebSearchEnabled(), "managed SearXNG real start should be enabled without an explicit flag");
                string arguments = LocalServer.BuildSearxngLauncherArguments(@"C:\kivrio\start_searxng.py", true);
                Assert(arguments.Contains("--json"), "managed launcher command should request JSON output");
                Assert(arguments.Contains("--real-start"), "managed launcher command should request real start for the managed backend");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", previousEnableManaged);
            }
        }

        private static void TestWebSearchMockLauncherDisabledByDefault()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-searxng-mock-disabled-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL");
            string previousEnableManaged = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED");
            string previousAllowMock = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK");
            string previousMockBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL");
            try
            {
                CreateFakeManagedSearxngRoot(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", null);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", null);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK", null);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL", "http://127.0.0.1:8030/");

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse response = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"message courant\"}"));
                string body = ResponseText(response);

                Assert(response.StatusCode == HttpStatusCode.OK, "disabled mock launcher should fail softly");
                Assert(body.Contains("\"available\":false"), "mock launcher should be disabled by default");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", previousBaseUrl);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", previousEnableManaged);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK", previousAllowMock);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL", previousMockBaseUrl);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestWebSearchMockLauncherRejectsNonLocalBaseUrl()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-searxng-mock-nonlocal-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL");
            string previousEnableManaged = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED");
            string previousAllowMock = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK");
            string previousMockBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL");
            try
            {
                CreateFakeManagedSearxngRoot(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", null);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", null);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK", "1");
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL", "http://example.com/");

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse response = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"message courant\"}"));
                string body = ResponseText(response);

                Assert(response.StatusCode == HttpStatusCode.OK, "non-local mock launcher URL should fail softly");
                Assert(body.Contains("\"available\":false"), "non-local mock launcher URL should be rejected");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", previousBaseUrl);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", previousEnableManaged);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK", previousAllowMock);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL", previousMockBaseUrl);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestWebSearchMockLauncherFullChain()
        {
            string root = Path.Combine(Path.GetTempPath(), "kivrio-chat-searxng-mock-chain-test-" + Guid.NewGuid().ToString("N"));
            string previousAuthFlag = Environment.GetEnvironmentVariable("KIVRO_DISABLE_AUTH");
            string previousBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL");
            string previousEnableManaged = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED");
            string previousAllowMock = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK");
            string previousMockBaseUrl = Environment.GetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL");
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            string receivedRequest = "";
            Exception serverException = null;
            try
            {
                CreateFakeManagedSearxngRoot(root);
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", "1");
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", null);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", null);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK", "1");

                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL", "http://127.0.0.1:" + port + "/");

                Thread serverThread = new Thread(delegate()
                {
                    try
                    {
                        using (TcpClient client = listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        {
                            receivedRequest = ReadHttpHeader(stream);
                            byte[] body = Encoding.UTF8.GetBytes(
                                "{\"results\":["
                                + "{\"title\":\"Mock Result\",\"url\":\"https://example.test/mock\",\"content\":\"Mock snippet\",\"engine\":\"mock\"}"
                                + "]}"
                            );
                            byte[] head = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\n"
                                + "Content-Type: application/json\r\n"
                                + "Content-Length: " + body.Length + "\r\n"
                                + "Connection: close\r\n\r\n"
                            );
                            stream.Write(head, 0, head.Length);
                            stream.Write(body, 0, body.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        serverException = ex;
                    }
                });
                serverThread.IsBackground = true;
                serverThread.Start();

                var server = new LocalServer(root, "127.0.0.1", 8020);
                HttpResponse response = InvokeRouteApi(server, JsonRequest(
                    "POST",
                    "/api/web-search",
                    "127.0.0.1:8020",
                    "http://127.0.0.1:8020",
                    "{\"query\":\"phase cinq\",\"max_results\":5}"));
                serverThread.Join(5000);
                string bodyText = ResponseText(response);

                Assert(response.StatusCode == HttpStatusCode.OK, "mock launcher chain should return OK");
                Assert(serverException == null, "mock launcher fake SearXNG server should not fail");
                Assert(receivedRequest.Contains("GET /search?"), "mock launcher chain should call the local SearXNG search path");
                Assert(receivedRequest.Contains("q=phase%20cinq"), "mock launcher chain should send only the current query");
                Assert(bodyText.Contains("\"ok\":true"), "mock launcher chain should report success");
                Assert(bodyText.Contains("\"available\":true"), "mock launcher chain should report availability");
                Assert(bodyText.Contains("\"title\":\"Mock Result\""), "mock launcher chain should normalize fake SearXNG results");
            }
            finally
            {
                Environment.SetEnvironmentVariable("KIVRO_DISABLE_AUTH", previousAuthFlag);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_BASE_URL", previousBaseUrl);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ENABLE_MANAGED", previousEnableManaged);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_ALLOW_MOCK", previousAllowMock);
                Environment.SetEnvironmentVariable("KIVRIO_WEB_SEARCH_MOCK_BASE_URL", previousMockBaseUrl);
                try { listener.Stop(); } catch { }
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

        private static void CreateFakeManagedSearxngRoot(string root)
        {
            string pythonDir = Path.Combine(root, "runtime", "python");
            string launcherDir = Path.Combine(root, "integrations", "searxng", "launcher");
            Directory.CreateDirectory(pythonDir);
            Directory.CreateDirectory(launcherDir);
            File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "", Encoding.UTF8);
            File.WriteAllText(Path.Combine(launcherDir, "start_searxng.py"), "", Encoding.UTF8);
            File.WriteAllText(Path.Combine(launcherDir, "stop_searxng.py"), "", Encoding.UTF8);
        }

        private static string ReadHttpHeader(NetworkStream stream)
        {
            var buffer = new List<byte>();
            int matched = 0;
            byte[] marker = new byte[] { 13, 10, 13, 10 };
            while (buffer.Count < 65536)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                byte current = (byte)value;
                buffer.Add(current);
                if (current == marker[matched])
                {
                    matched++;
                    if (matched == marker.Length) break;
                }
                else
                {
                    matched = current == marker[0] ? 1 : 0;
                }
            }
            return Encoding.ASCII.GetString(buffer.ToArray());
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

        private static string ExtractSessionCookie(HttpResponse response)
        {
            string setCookie;
            Assert(response.Headers.TryGetValue("Set-Cookie", out setCookie), "login should return a session cookie");
            int separator = setCookie.IndexOf(';');
            return separator >= 0 ? setCookie.Substring(0, separator) : setCookie;
        }

        private static bool GetPrivateBool(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "private field should exist: " + fieldName);
            return (bool)field.GetValue(instance);
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
