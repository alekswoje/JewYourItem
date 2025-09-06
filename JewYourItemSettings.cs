using System;
using System.Collections.Generic;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using System.Numerics;
using System.Windows.Forms;

namespace JewYourItem;

[Submenu(CollapsedByDefault = false)]
public class JewYourItemSettings : ISettings
{
    public JewYourItemSettings()
    {
        GroupsConfig = new GroupsRenderer(this);
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    [IgnoreMenu]
    public TextNode SessionId { get; set; } = new TextNode("");
    [Menu("Travel Hotkey", "Key to initiate travel action")]
    [IgnoreMenu]
    public HotkeyNode TravelHotkey { get; set; } = new HotkeyNode(Keys.F5);
    [Menu("Play Sound", "Enable sound alerts for new items")]
    [IgnoreMenu]
    public ToggleNode PlaySound { get; set; } = new ToggleNode(true);
    [Menu("Auto Teleport", "Automatically teleport to items")]
    [IgnoreMenu]
    public ToggleNode AutoTp { get; set; } = new ToggleNode(false);
    [Menu("Show GUI", "Display the graphical user interface")]
    [IgnoreMenu]
    public ToggleNode ShowGui { get; set; } = new ToggleNode(true);
    [Menu("Teleport Cooldown (s)", "Cooldown period between teleports (1-30s)")]
    [IgnoreMenu]
    public RangeNode<int> TpCooldown { get; set; } = new RangeNode<int>(10, 1, 30);
    [Menu("Move Mouse to Item", "Move mouse cursor to highlighted items")]
    [IgnoreMenu]
    public ToggleNode MoveMouseToItem { get; set; } = new ToggleNode(false);
    public Vector2 WindowPosition { get; set; } = new Vector2(10, 800);
    public List<SearchGroup> Groups { get; set; } = new List<SearchGroup>();
    [JsonIgnore]
    public GroupsRenderer GroupsConfig { get; set; }
    [Menu("Cancel With Right Click", "Cancel operation on manual right-click")]
    [IgnoreMenu]
    public ToggleNode CancelWithRightClick { get; set; } = new ToggleNode(true);
    
    [Submenu(RenderMethod = nameof(Render))]
    public class GroupsRenderer
    {
        private readonly JewYourItemSettings _parent;
        private readonly Dictionary<string, string> _groupNameBuffers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _searchNameBuffers = new Dictionary<string, string>();

        public GroupsRenderer(JewYourItemSettings parent)
        {
            _parent = parent;
        }

        private static void HelpMarker(string desc)
        {
            if (!string.IsNullOrEmpty(desc))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
                    ImGui.TextUnformatted(desc);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
            }
        }

        public void Render()
        {
            var enable = _parent.Enable.Value;
            ImGui.Checkbox("Enable", ref enable);
            _parent.Enable.Value = enable;
            ImGui.Text("Groups:");
            ImGui.Separator();
            var tempGroups = new List<SearchGroup>(_parent.Groups);
            for (int i = 0; i < tempGroups.Count; i++)
            {
                var group = tempGroups[i];
                var groupIdKey = $"group{i}";
                if (!_groupNameBuffers.ContainsKey(groupIdKey))
                {
                    _groupNameBuffers[groupIdKey] = group.Name.Value;
                }
                var groupNameBuffer = _groupNameBuffers[groupIdKey];
                groupNameBuffer = group.Name.Value; // Sync buffer with current value
                if (group.Enable.Value)
                {
                    ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.0f, 0.8f, 0.0f, 1.0f));
                }
                bool isOpen = ImGui.CollapsingHeader($"Group: {group.Name.Value}##group{i}");
                if (_parent.CancelWithRightClick.Value && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    ImGui.OpenPopup($"RemoveGroupContext{i}");
                }
                if (ImGui.BeginPopup($"RemoveGroupContext{i}"))
                {
                    if (ImGui.Selectable("Remove Group"))
                    {
                        tempGroups.RemoveAt(i);
                        _groupNameBuffers.Remove(groupIdKey);
                        i--;
                    }
                    ImGui.EndPopup();
                }
                if (isOpen)
                {
                    if (group.Enable.Value)
                    {
                        ImGui.PopStyleColor(2);
                    }
                    ImGui.Indent();
                    if (ImGui.InputText($"Name##group{i}", ref groupNameBuffer, 100))
                    {
                        group.Name.Value = groupNameBuffer;
                    }
                    var enableGroup = group.Enable.Value;
                    ImGui.Checkbox($"Enable##group{i}", ref enableGroup);
                    group.Enable.Value = enableGroup;
                    HelpMarker("Enable or disable this group; right-click header to delete group");
                    var url = group.TradeUrl.Value.Trim();
                    string urlBuffer = url;
                    if (ImGui.InputText($"Add from URL##group{i}", ref urlBuffer, 100))
                    {
                        group.TradeUrl.Value = urlBuffer;
                    }
                    HelpMarker("Enter a trade search URL to add searches");
                    if (ImGui.Button($"Add Search from URL##group{i}"))
                    {
                        if (string.IsNullOrWhiteSpace(urlBuffer))
                        {
                            ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: URL cannot be empty.");
                        }
                        else
                        {
                            Uri uri;
                            try
                            {
                                uri = new Uri(urlBuffer.StartsWith("http") ? urlBuffer : $"https://www.pathofexile.com/trade2/search/poe2/Rise%20of%20the%20Abyssal/{urlBuffer}/live");
                            }
                            catch (UriFormatException)
                            {
                                ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: Invalid URL format.");
                                return;
                            }
                            var segments = uri.AbsolutePath.TrimStart('/').Split('/');
                            if (segments.Length >= 5 && segments[0] == "trade2" && segments[1] == "search" && segments[2] == "poe2" && (segments.Length == 5 || segments[5] == "live"))
                            {
                                var league = Uri.UnescapeDataString(segments[3]);
                                var searchId = segments[4];
                                group.Searches.Add(new JewYourItemInstanceSettings
                                {
                                    League = new TextNode(league),
                                    SearchId = new TextNode(searchId),
                                    Name = new TextNode($"Search {group.Searches.Count + 1}"),
                                    Enable = new ToggleNode(false)
                                });
                                ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), $"Added search: {searchId} in {league}");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: URL must match trade search format.");
                            }
                            group.TradeUrl.Value = "";
                        }
                    }
                    var tempSearches = new List<JewYourItemInstanceSettings>(group.Searches);
                    for (int j = 0; j < tempSearches.Count; j++)
                    {
                        var search = tempSearches[j];
                        var searchIdKey = $"search{i}{j}";
                        if (!_searchNameBuffers.ContainsKey(searchIdKey))
                        {
                            _searchNameBuffers[searchIdKey] = search.Name.Value;
                        }
                        var searchNameBuffer = _searchNameBuffers[searchIdKey];
                        searchNameBuffer = search.Name.Value; // Sync buffer with current value
                        if (search.Enable.Value)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
                            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.0f, 0.8f, 0.0f, 1.0f));
                        }
                        bool sOpen = ImGui.CollapsingHeader($"Search: {searchNameBuffer}##search{i}{j}");
                        if (_parent.CancelWithRightClick.Value && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        {
                            tempSearches.RemoveAt(j);
                            _searchNameBuffers.Remove(searchIdKey);
                            j--;
                        }
                        if (sOpen)
                        {
                            if (search.Enable.Value)
                            {
                                ImGui.PopStyleColor(2);
                            }
                            ImGui.Indent();
                            var senable = search.Enable.Value;
                            ImGui.Checkbox($"Enable##search{i}{j}", ref senable);
                            search.Enable.Value = senable;
                            HelpMarker("Enable or disable this search; right-click header to delete search");
                            if (ImGui.InputText($"Name##search{i}{j}", ref searchNameBuffer, 100))
                            {
                                search.Name.Value = searchNameBuffer;
                            }
                            var league = search.League.Value;
                            ImGui.InputText($"League##search{i}{j}", ref league, 100);
                            search.League.Value = league;
                            HelpMarker("League for this search");
                            var searchId = search.SearchId.Value;
                            ImGui.InputText($"Search ID##search{i}{j}", ref searchId, 100);
                            search.SearchId.Value = searchId;
                            HelpMarker("Unique ID for the trade search");
                            ImGui.Unindent();
                        }
                        else if (search.Enable.Value)
                        {
                            ImGui.PopStyleColor(2);
                        }
                    }
                    group.Searches = tempSearches;
                    ImGui.Unindent();
                }
                else if (group.Enable.Value)
                {
                    ImGui.PopStyleColor(2);
                }
                ImGui.Separator();
            }
            _parent.Groups = tempGroups;
            var newGroupName = _parent.NewGroupName.Value;
            ImGui.InputText("New Group Name", ref newGroupName, 100);
            HelpMarker("Name for a new group to be added");
            if (ImGui.Button("Add Group"))
            {
                _parent.Groups.Add(new SearchGroup
                {
                    Name = new TextNode(newGroupName),
                    Enable = new ToggleNode(false),
                    Searches = new List<JewYourItemInstanceSettings>(),
                    TradeUrl = new TextNode("")
                });
            }
            var pos = _parent.WindowPosition;
            ImGui.Text("Window Position:");
            ImGui.SameLine();
            float x = pos.X, y = pos.Y;
            ImGui.InputFloat("X##WindowPosX", ref x, 1.0f, 100.0f, "%.0f");
            ImGui.InputFloat("Y##WindowPosY", ref y, 1.0f, 100.0f, "%.0f");
            _parent.WindowPosition = new Vector2(x, y);
            HelpMarker("Position of the settings window on screen");
        }
    }

    [IgnoreMenu]
    public TextNode NewGroupName { get; set; } = new TextNode("New Group");
}

public class SearchGroup
{
    public TextNode Name { get; set; } = new TextNode("New Group");
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public List<JewYourItemInstanceSettings> Searches { get; set; } = new List<JewYourItemInstanceSettings>();
    public TextNode TradeUrl { get; set; } = new TextNode("");
}

public class JewYourItemInstanceSettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public TextNode Name { get; set; } = new TextNode("New Search");
    public TextNode League { get; set; } = new TextNode("Rise of the Abyssal");
    public TextNode SearchId { get; set; } = new TextNode("");
}