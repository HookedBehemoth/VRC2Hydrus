using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using LagFreeScreenshots;
using MelonLoader;
using Newtonsoft.Json;

[assembly: MelonInfo(typeof(HydrusScreenshotManager.Starter), nameof(HydrusScreenshotManager), "1.0.0", "Behemoth")]
[assembly: MelonGame("VRChat", "VRChat")]

namespace HydrusScreenshotManager {
    public class Starter : MelonMod {
        HttpClient client;
        MelonPreferences_Entry<string> domain;
        MelonPreferences_Entry<int> port;
        MelonPreferences_Entry<string> accessKey;
        MelonPreferences_Entry<bool> sendAsFile;

        public override void OnApplicationStart() {
            LoggerInstance.Msg("HydrusScreenshotManager loaded!");
            var category = MelonPreferences.CreateCategory("Hydrus");
            domain = category.CreateEntry("Domain", "http://localhost", "Domain");
            port = category.CreateEntry("Port", 45869, "Port");
            accessKey = category.CreateEntry("AccessKey", "", "Access Key");
            sendAsFile = category.CreateEntry("SendAsFile", false, "Send as file");

            domain.OnValueChanged += (_, _) => {LoggerInstance.Msg("domain changed"); ConfigureClient();};
            port.OnValueChanged += (_, _) => {LoggerInstance.Msg("port changed"); ConfigureClient();};
            accessKey.OnValueChanged += (_, _) => {LoggerInstance.Msg("key changed"); ConfigureClient();};

            ConfigureClient();
        }

        void ConfigureClient() {
            /* Unsubscribe in advance */
            LagFreeScreenshotsMod.OnScreenshotTaken -= OnScreenshotTaken;
            if (domain.Value == null || port.Value == 0 || accessKey.Value == null) {
                LoggerInstance.Error("Hydrus not configured");
                client = null;
                return;
            }

            client = new HttpClient
            {
                BaseAddress = new Uri($"{domain.Value}:{port.Value}")
            };

            /* Set header */
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("Hydrus-Client-API-Access-Key", accessKey.Value);

            VerifyAccessKey().ContinueWith(t => {
                if (t.IsFaulted) {
                    LoggerInstance.Error($"Failed to verify access key: {t.Exception.Message}");
                    client = null;
                    return;
                }
            });
        }

        private void OnScreenshotTaken(string filepath, string[] playerList) {
            UploadAndTagScreenshot(filepath, playerList).ContinueWith(t => {
                if (t.IsFaulted) {
                    LoggerInstance.Error("Failed to upload screenshot");
                    return;
                }
            });
        }

        private async Task UploadAndTagScreenshot(string filepath, string[] playerList) {
            var hash = await AddFile(filepath);
            var comb = playerList.Select(player => $"person:{player}")
                .AddItem("game:VRChat")
                .AddItem($"map:{RoomManager.field_Internal_Static_ApiWorld_0.name}");
            await AddTags(hash, comb.ToArray());
        }

        private struct VerifyAccessKeyResponse {
            public int[] basic_permissions;
            public string human_description;
        }

        private async Task VerifyAccessKey() {
            /* Make request */
            var response = await client.GetAsync("/verify_access_key");

            /* Parse response */
            if (!response.IsSuccessStatusCode) {
                LoggerInstance.Error($"Couldn't verify access key! {response.StatusCode}: {response.ReasonPhrase}");
                switch (response.StatusCode) {
                    case HttpStatusCode.Unauthorized:
                    case HttpStatusCode.Forbidden:
                    case (HttpStatusCode)419:
                        LoggerInstance.Error("Access key is invalid");
                        return;
                    default:
                        LoggerInstance.Error($"Failed to access server at {domain.Value}:{port.Value}");
                        return;
                }
            }

            /* Verify permissions */
            string text = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonConvert.DeserializeObject<VerifyAccessKeyResponse>(text);
            var permissions = jsonResponse.basic_permissions;
            if (!permissions.Contains(1) || !permissions.Contains(2)) {
                LoggerInstance.Error("Access key doesn't have the required permissions \"Add Tags\" and \"Add Files\"!");
                LoggerInstance.Msg(jsonResponse.human_description);
                return;
            }

            LagFreeScreenshotsMod.OnScreenshotTaken += OnScreenshotTaken;
        }

        private enum AddFileResponseStatus {
            Ok = 1,
            FileExists = 2,
            FileDeleted = 3,
            ImportFailed = 4,
            FileVetoed = 7,
        };
        private struct AddFileResponse {
            public AddFileResponseStatus status;
            public string hash;
            public string note;
        }

        private async Task<string> AddFile(string path) {
            /* Prepare data */
            HttpContent content = sendAsFile.Value switch {
                true => MakeRawFileContent(path),
                false => MakeJsonContent(new { path }),
            };

            /* Make request */
            var response = await client.PostAsync("/add_files/add_file", content);

            /* Parse response */
            if (!response.IsSuccessStatusCode)
                throw new Exception($"HTTP Error: {response.StatusCode}");

            string text = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonConvert.DeserializeObject<AddFileResponse>(text);
            return jsonResponse.status switch {
                AddFileResponseStatus.Ok => jsonResponse.hash,
                _ => throw new Exception($"Hydrus: {jsonResponse.status}, note: {jsonResponse.note}"),
            };
        }

        private async Task AddTags(string hash, string[] tags) {
            /* Prepare data */
            var content = MakeJsonContent(new
            {
                hash,
                service_names_to_tags = new Dictionary<string, string[]> { { "my tags", tags } }
            });

            /* Make request */
            var response = await client.PostAsync("/add_tags/add_tags", content);

            /* Parse response */
            if (!response.IsSuccessStatusCode)
                throw new Exception($"HTTP Error: {response.StatusCode}");
        }

        private static HttpContent MakeJsonContent(object obj)
            => new StringContent(JsonConvert.SerializeObject(obj), Encoding.UTF8, "application/json");

        private static HttpContent MakeRawFileContent(string path) {
            var content = new StreamContent(new FileStream(path, FileMode.Open));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return content;
        }
    }
}
