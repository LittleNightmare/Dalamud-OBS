﻿using Dalamud.Logging;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using Newtonsoft.Json.Linq;
using OBSPlugin.Objects;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace OBSPlugin
{
    public class PluginUI
    {
        private readonly Plugin Plugin;
        public PluginUI(Plugin plugin)
        {
            Plugin = plugin;
        }
        public Configuration Config => Plugin.config;
        private int UIErrorCount = 0;

        public bool IsVisible { get; set; }

        public void Draw()
        {
            if (Config.UIDetection)
                UpdateGameUI();

            if (!IsVisible)
                return;

            ImGui.SetNextWindowSize(new Vector2(530, 450), ImGuiCond.FirstUseEver);
            bool configOpen = IsVisible;
            if (ImGui.Begin("OBS Plugin Config", ref configOpen))
            {
                IsVisible = configOpen;
                if (ImGui.BeginTabBar("TabBar"))
                {
                    if (ImGui.BeginTabItem("Connection##Tab"))
                    {
                        if (ImGui.BeginChild("Connection##SettingsRegion"))
                        {
                            DrawConnectionSettings();
                            ImGui.EndChild();
                        }
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Stream##Tab"))
                    {
                        if (ImGui.BeginChild("Blur##SettingsRegion"))
                        {
                            DrawStream();
                            ImGui.EndChild();
                        }
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Blur##Tab"))
                    {
                        if (ImGui.BeginChild("Blur##SettingsRegion"))
                        {
                            DrawBlurSettings();
                            ImGui.EndChild();
                        }
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("About##Tab"))
                    {
                        if (ImGui.BeginChild("Blur##SettingsRegion"))
                        {
                            DrawAbout();
                            ImGui.EndChild();
                        }
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
                ImGui.End();
            }

        }

        private unsafe (float, float, float, float) GetUIRect(float X, float Y, float W, float H)
        {
            var size = ImGui.GetIO().DisplaySize;
            var width = size.X;
            var height = size.Y;
            var top = Y / height * 100;
            var left = X / width * 100;
            var bottom = (height - Y - H) / height * 100;
            var right = (width - X - W) / width * 100;
            return (top, bottom, left, right);
        }

        private bool AddOrUpdateBlur(Blur blur)
        {
            if (!Plugin.obs.IsConnected) return false;
            string sourceName = Config.SourceName;
            try
            {
                var filters = Plugin.obs.GetSourceFilters(sourceName);
                var filter = filters.Find(f => f.Name == blur.Name);
                var settings = filter == null ? new JObject() : filter.Settings;
                settings["Filter.Blur.Mask"] = true;
                settings["Filter.Blur.Mask.Region.Top"] = blur.Top;
                settings["Filter.Blur.Mask.Region.Bottom"] = blur.Bottom;
                settings["Filter.Blur.Mask.Region.Left"] = blur.Left;
                settings["Filter.Blur.Mask.Region.Right"] = blur.Right;
                settings["Filter.Blur.Mask.Type"] = 0;
                settings["Filter.Blur.Size"] = blur.Size;
                if (filter == null)
                {
                    Plugin.obs.AddFilterToSource(sourceName, blur.Name, "streamfx-filter-blur", settings);
                } else
                {
                    Plugin.obs.SetSourceFilterSettings(sourceName, blur.Name, settings);
                }
                Plugin.obs.SetSourceFilterVisibility(sourceName, blur.Name, blur.Enabled);
            }
            catch (ErrorResponseException e)
            {
                if (e.ToString().Contains("specified source doesn't exist"))
                {
                    Config.UIDetection = false;
                    var errMsg = $"Cannot find source \"{Config.SourceName}\", please check.";
                    PluginLog.Error(errMsg);
                    Plugin.Chat.PrintError($"[OBSPlugin] {errMsg}");
                    Config.Save();
                }
                return false;
            }
            catch (Exception e)
            {
                PluginLog.Error("Failed updating blur: {0}", e);
                return false;
            }
            PluginLog.Debug("Updated blur: {0}", blur.Name);
            return true;
        }

        private bool RemoveBlur(Blur blur)
        {
            if (!Plugin.obs.IsConnected) return false;
            bool removed = false;
            try
            {
                removed = Plugin.obs.RemoveFilterFromSource(Config.SourceName, blur.Name);
                PluginLog.Debug("Deleted blur: {0}", blur.Name);
            }
            catch (Exception e)
            {
                PluginLog.Error("Failed deleting blur: {0}", e);
                return false;
            }
            return removed;
        }

        internal unsafe void UpdateGameUI()
        {
            if (!Config.Enabled) return;
            if (Plugin.ClientState.LocalPlayer == null) return;
            try
            {
                UpdateChatLog();
            }
            catch (Exception e)
            {
                PluginLog.Error("Error Updating ChatLog UI: {0}", e);
                Config.ChatLogBlur = false;
                UIErrorCount++;
                Config.Save();
            }
            try
            {
                UpdatePartyList();
            }
            catch (Exception e)
            {
                PluginLog.Error("Error Updating PartyList UI: {0}", e);
                Config.PartyListBlur = false;
                UIErrorCount++;
                Config.Save();
            }
            try
            {
                UpdateTarget();
            }
            catch (Exception e)
            {
                PluginLog.Error("Error Updating Target UI: {0}", e);
                Config.TargetBlur = false;
                Config.TargetTargetBlur = false;
                UIErrorCount++;
                Config.Save();
            }
            if (UIErrorCount > 1000)
            {
                var errMsg = "More than 1000 UI errors encountered, UI detection is turned off. " +
                    "Please open /xllog for more details.";
                PluginLog.Error(errMsg);
                Plugin.Chat.PrintError($"[OBSPlugin] {errMsg}");
                Config.UIDetection = false;
                Config.Save();
            }
        }

        private unsafe void UpdateChatLog()
        {
            if (!Config.ChatLogBlur) return;
            var chatLogBlur = Config.BlurList.Find(blur => blur.Name == "ChatLog");
            var chatLogAddress = Plugin.GameGui.GetAddonByName("ChatLog", 1);
            if (chatLogAddress == IntPtr.Zero) return;
            bool created = false;
            if (chatLogBlur == null)
            {
                chatLogBlur = new Blur("ChatLog", 0, 0, 0, 0, Config.BlurSize);
                created = true;
            }
            var chatLog = (AtkUnitBase*)chatLogAddress;
            bool isVisible = (chatLog->Flags & 0x20) == 0x20;
            var (top, bottom, left, right) = GetUIRect(chatLog->X, chatLog->Y, chatLog->RootNode->Width, chatLog->RootNode->Height);
            if (chatLogBlur.Top != top
                || chatLogBlur.Bottom != bottom
                || chatLogBlur.Left != left
                || chatLogBlur.Right != right
                || chatLogBlur.Size != Config.BlurSize
                || chatLogBlur.Enabled != isVisible)
            {
                chatLogBlur.Enabled = isVisible;
                chatLogBlur.Top = top;
                chatLogBlur.Bottom = bottom;
                chatLogBlur.Left = left;
                chatLogBlur.Right = right;
                chatLogBlur.Size = Config.BlurSize;
                if (AddOrUpdateBlur(chatLogBlur))
                {
                    if (created)
                    {
                        Config.BlurList.Add(chatLogBlur);
                    }
                    Config.Save();
                }
            }
        }

        private unsafe Vector2 GetNodePosition(AtkResNode* node)
        {
            var pos = new Vector2(node->X, node->Y);
            var par = node->ParentNode;
            while (par != null)
            {
                pos *= new Vector2(par->ScaleX, par->ScaleY);
                pos += new Vector2(par->X, par->Y);
                par = par->ParentNode;
            }

            return pos;
        }

        private unsafe Vector2 GetNodeScale(AtkResNode* node)
        {
            if (node == null) return new Vector2(1, 1);
            var scale = new Vector2(node->ScaleX, node->ScaleY);
            while (node->ParentNode != null)
            {
                node = node->ParentNode;
                scale *= new Vector2(node->ScaleX, node->ScaleY);
            }

            return scale;
        }

        private unsafe bool GetNodeVisible(AtkResNode* node)
        {
            if (node == null) return false;
            while (node != null)
            {
                if ((node->Flags & (short)NodeFlags.Visible) != (short)NodeFlags.Visible) return false;
                node = node->ParentNode;
            }

            return true;
        }

        private unsafe Blur GetBlurFromNode(AtkResNode* node, string name)
        {
            var position = GetNodePosition(node);
            var scale = GetNodeScale(node);
            var size = new Vector2(node->Width, node->Height) * scale;
            var (top, bottom, left, right) = GetUIRect(position.X,
                position.Y,
                size.X,
                size.Y);
            Blur blur = new(name, top, bottom, left, right, Config.BlurSize);
            return blur;
        }
        private unsafe bool UpdateConfigBlur(Blur blur)
        {
            var _blur = Config.BlurList.Find(confBlur => confBlur.Name == blur.Name);
            if (_blur == null
                || _blur.Enabled != blur.Enabled
                || _blur.Top != blur.Top
                || _blur.Bottom != blur.Bottom
                || _blur.Left != blur.Left
                || _blur.Right != blur.Right
                || _blur.Size != blur.Size)
            {
                if (AddOrUpdateBlur(blur))
                {
                    if (_blur == null)
                    {
                        Config.BlurList.Add(blur);
                    }
                    else
                    {
                        _blur.Enabled = blur.Enabled;
                        _blur.Top = blur.Top;
                        _blur.Bottom = blur.Bottom;
                        _blur.Left = blur.Left;
                        _blur.Right = blur.Right;
                        _blur.Size = blur.Size;
                    }
                    return true;
                }
            }
            return false;
        }

        private unsafe void UpdatePartyList()
        {
            if (!Config.PartyListBlur) return;
            Blur[] partyMemberBlurList = new Blur[8];
            HashSet<string> existingBlur = new();
            uint partyMemberCount = 0;
            var partyListAddress = Plugin.GameGui.GetAddonByName("_PartyList", 1);
            if (partyListAddress == IntPtr.Zero) return;
            var partyList = (AtkUnitBase*)partyListAddress;
            for (var i = 0; i < partyList->UldManager.NodeListCount; i++)
            {
                var childNode = partyList->UldManager.NodeList[i];
                var IsVisible = GetNodeVisible(childNode);
                if (childNode != null && (int)childNode->Type == 1006 && IsVisible)
                {
                    for (var j = 0; j < childNode->GetAsAtkComponentNode()->Component->UldManager.NodeListCount; j++)
                    {
                        var childChildNode = childNode->GetAsAtkComponentNode()->Component->UldManager.NodeList[j];
                        var childChildIsVisible = GetNodeVisible(childChildNode);
                        if (childChildNode != null && childChildNode->Type == NodeType.Text && childChildIsVisible)
                        {
                            Blur blur = GetBlurFromNode(childChildNode, $"PartyList_{partyMemberCount}");
                            partyMemberBlurList[partyMemberCount] = blur;
                            existingBlur.Add($"PartyList_{partyMemberCount}");
                            partyMemberCount++;
                            break;
                        }
                    }
                }
            }
            Config.BlurList.RemoveAll(blur => {
                bool toDel = blur.Name.StartsWith("PartyList") && !existingBlur.Contains(blur.Name);
                if (toDel) RemoveBlur(blur);
                return toDel;
            });
            bool toSave = false;
            for (uint i = 0; i < partyMemberCount; i++)
            {
                var blur = partyMemberBlurList[i];
                toSave |= UpdateConfigBlur(blur);
            }
            if (toSave) Config.Save();
        }


        private unsafe void UpdateTarget()
        {
            if (!Config.TargetBlur && !Config.TargetTargetBlur) return;
            Blur targetBlur = null;
            Blur targetTargetBlur = null;
            uint partyMemberCount = 0;
            var targetInfoAddress = Plugin.GameGui.GetAddonByName("_TargetInfo", 1);
            if (targetInfoAddress == IntPtr.Zero) return;
            var targetInfo = (AtkUnitBase*)targetInfoAddress;
            int textIndex = 0;
            for (var i = 0; i < targetInfo->UldManager.NodeListCount; i++)
            {
                var childNode = targetInfo->UldManager.NodeList[i];
                var IsVisible = GetNodeVisible(childNode);
                if (childNode != null && childNode->Type == NodeType.Text && IsVisible)
                {
                    if (textIndex == 1)
                    {
                        targetBlur = GetBlurFromNode(childNode, "Target");
                    } else if (textIndex == 2)
                    {
                        targetTargetBlur = GetBlurFromNode(childNode, "TargetTarget");
                    }
                    textIndex++;
                }
            }
            bool toSave = false;
            if (Config.TargetBlur)
            {
                if (targetBlur == null)
                {
                    Config.BlurList.RemoveAll(blur => {
                        bool toDel = blur.Name == "Target";
                        if (toDel) RemoveBlur(blur);
                        return toDel;
                    });
                }
                else
                {
                    toSave |= UpdateConfigBlur(targetBlur);
                }
            }

            if (Config.TargetTargetBlur)
            {
                if (targetTargetBlur == null)
                {
                    Config.BlurList.RemoveAll(blur => {
                        bool toDel = blur.Name == "TargetTarget";
                        if (toDel) RemoveBlur(blur);
                        return toDel;
                    });
                }
                else
                {
                    toSave |= UpdateConfigBlur(targetTargetBlur);
                }
            }
                
            if (toSave) Config.Save();
        }

        private void DrawConnectionSettings()
        {
            if (ImGui.Checkbox("Enabled", ref Config.Enabled))
            {
                Config.Save();
            }
            ImGui.SameLine(ImGui.GetColumnWidth() - 80);
            ImGui.TextColored(Plugin.obs.IsConnected ? new(0, 1, 0, 1) : new(1, 0, 0, 1),
                Plugin.obs.IsConnected ? "Connected" : "Disconnected");
            if (ImGui.InputText("Server Address", ref Config.Address, 128))
            {
                Config.Save();
            }
            if (ImGui.InputText("Password", ref Config.Password, 128, ImGuiInputTextFlags.Password))
            {
                Config.Save();
            }
            string connectionButtonText = Plugin.obs.IsConnected ? "Disconnect" : "Connect";
            if (ImGui.Button(connectionButtonText))
            {
                if (Plugin.obs.IsConnected)
                {
                    Plugin.obs.Disconnect();
                } else
                {
                    Plugin.TryConnect(Config.Address, Config.Password);
                }
            }
            if (Plugin.ConnectionFailed)
            {
                ImGui.SameLine();
                ImGui.Text("Authentication failed, check the address and password!");
            }
        }

        private void DrawBlurSettings()
        {
            if (ImGui.Checkbox("UI Detection", ref Config.UIDetection))
            {
                UIErrorCount = 0;
                Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Detect game UI elements together with game-rendering.\n" +
                    "May cause performance issues.\n"+
                    "If disabled, you need to manually call \"/obs update\" to update the blurs in obs.");
            if (ImGui.InputText("Source Name", ref Config.SourceName, 128))
            {
                Config.Save();
            }
            if (ImGui.DragInt("Blur Size", ref Config.BlurSize, 1, 1, 128))
            {
                foreach(var blur in Config.BlurList)
                {
                    blur.Size = Config.BlurSize;
                    AddOrUpdateBlur(blur);
                }
                Config.Save();
            }
            ImGui.Separator();
            if (ImGui.Checkbox("ChatLog", ref Config.ChatLogBlur))
            {
                if (!Config.ChatLogBlur)
                {
                    var chatLogBlur = Config.BlurList.Find(blur => blur.Name == "ChatLog");
                    if (chatLogBlur != null)
                    {
                        chatLogBlur.Enabled = false;
                        AddOrUpdateBlur(chatLogBlur);
                        PluginLog.Debug("Turn off {0}", chatLogBlur.Name);
                    }
                }
                Config.Save();
            }
            if (ImGui.Checkbox("PartyList", ref Config.PartyListBlur))
            {
                if (!Config.PartyListBlur)
                {
                    var partyListBlurs = Config.BlurList.FindAll(blur => blur.Name.StartsWith("PartyList"));
                    for (int i = 0; i < Config.BlurList.Count; i++)
                    {
                        if (Config.BlurList[i].Name.StartsWith("PartyList"))
                        {
                            Config.BlurList[i].Enabled = false;
                            AddOrUpdateBlur(Config.BlurList[i]);
                            PluginLog.Debug("Turn off {0}", Config.BlurList[i].Name);
                        }
                    }
                }
                Config.Save();
            }
            if (ImGui.Checkbox("Target", ref Config.TargetBlur))
            {
                if (!Config.TargetBlur)
                {
                    var targetBlur = Config.BlurList.Find(blur => blur.Name == "Target");
                    if (targetBlur != null)
                    {
                        targetBlur.Enabled = false;
                        AddOrUpdateBlur(targetBlur);
                        PluginLog.Debug("Turn off {0}", targetBlur);
                    }
                }
                Config.Save();
            }
            if (ImGui.Checkbox("TargetTarget", ref Config.TargetTargetBlur))
            {
                if (!Config.TargetTargetBlur)
                {
                    var targetTargetBlur = Config.BlurList.Find(blur => blur.Name == "TargetTarget");
                    if (targetTargetBlur != null)
                    {
                        targetTargetBlur.Enabled = false;
                        AddOrUpdateBlur(targetTargetBlur);
                        PluginLog.Debug("Turn off {0}", targetTargetBlur);
                    }
                }
                Config.Save();
            }

        }

        private void DrawAbout()
        {
            ImGui.Text("This plugin is still WIP, lot of functions are still in development.");

            ImGui.Text("You need to install two plugins in your OBS for this plugin to work:");

            ImGui.BulletText("");
            ImGui.SameLine();
            if (ImGui.Button("StreamFX"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        FileName = "https://github.com/Xaymar/obs-StreamFX/releases/latest",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "Could not open StreamFX url");
                }
            }
            ImGui.SameLine();
            ImGui.Text("Just download and install.");

            ImGui.BulletText("");
            ImGui.SameLine();
            if (ImGui.Button("OBS-websocket"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        FileName = "https://github.com/Palakis/obs-websocket/releases/latest",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "Could not open OBS-websocket url");
                }
            }
            ImGui.SameLine();
            ImGui.Text("You need to set a password and provide in the #Connection tab.");

            ImGui.NewLine();
            ImGui.Text("If you encountered any bugs please submit issues in");
            ImGui.SameLine();
            if (ImGui.Button("Github"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        FileName = "https://github.com/Bluefissure/Dalamud-OBS",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "Could not open OBS-websocket url");
                }
            }
        }

        private void DrawStream()
        {
            string obsButtonText;

            switch (Plugin.obsStatus)
            {
                case OutputState.Starting:
                    obsButtonText = "Stream starting...";
                    break;

                case OutputState.Started:
                    obsButtonText = "Stop streaming";
                    break;

                case OutputState.Stopping:
                    obsButtonText = "Stream stopping...";
                    break;

                case OutputState.Stopped:
                    obsButtonText = "Start streaming";
                    break;

                default:
                    obsButtonText = "State unknown";
                    break;
            }

            if (ImGui.Button(obsButtonText))
            {
                Plugin.obs.ToggleStreaming();
            }

            if (Plugin.streamStatus == null) return;

            ImGui.Text($"Stream time : {Plugin.streamStatus.TotalStreamTime} sec");
            ImGui.Text($"Kbits/sec : {Plugin.streamStatus.KbitsPerSec} kbit/s");
            ImGui.Text($"Bytes/sec : {Plugin.streamStatus.BytesPerSec} bytes/s");
            ImGui.Text($"Framerate : {Plugin.streamStatus.BytesPerSec} %");
            ImGui.Text($"Strain : {Plugin.streamStatus.Strain} FPS");
            ImGui.Text($"Dropped frames : {Plugin.streamStatus.DroppedFrames}");
            ImGui.Text($"Total frames : {Plugin.streamStatus.TotalFrames}");
        }
    }
}
