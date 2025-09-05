using System.Collections.Generic;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;

namespace LiveSearch;

[Submenu(CollapsedByDefault = false)]
public class LiveSearchSettings : ISettings
{
    public LiveSearchSettings()
    {
        GroupsConfig = new GroupsRenderer(this);
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    [IgnoreMenu]
    public TextNode GlobalSessionId { get; set; } = new TextNode("");
    [IgnoreMenu]
    public TextNode CfClearance { get; set; } = new TextNode("");
    public TextNode TradeUrl { get; set; } = new TextNode("");
    public ButtonNode AddFromUrl { get; set; } = new ButtonNode();
    public List<SearchGroup> Groups { get; set; } = new List<SearchGroup>();

    [JsonIgnore]
    public GroupsRenderer GroupsConfig { get; set; }

    [Submenu(RenderMethod = nameof(Render))]
    public class GroupsRenderer
    {
        private readonly LiveSearchSettings _parent;

        public GroupsRenderer(LiveSearchSettings parent)
        {
            _parent = parent;
        }

        public void Render()
        {
            ImGui.Text("Groups:");
            ImGui.Separator();

            var tempGroups = new List<SearchGroup>(_parent.Groups);

            for (int i = 0; i < tempGroups.Count; i++)
            {
                var group = tempGroups[i];

                var name = group.Name.Value;
                ImGui.InputText($"Name##group{i}", ref name, 100);
                group.Name.Value = name;

                var enable = group.Enable.Value;
                ImGui.Checkbox($"Enable##group{i}", ref enable);
                group.Enable.Value = enable;

                if (ImGui.Button($"Remove Group##{i}"))
                {
                    tempGroups.RemoveAt(i);
                    i--;
                    continue;
                }

                ImGui.Indent();

                if (ImGui.Button($"Add Search##group{i}"))
                {
                    group.Searches.Add(new LiveSearchInstanceSettings());
                }

                var tempSearches = new List<LiveSearchInstanceSettings>(group.Searches);
                for (int j = 0; j < tempSearches.Count; j++)
                {
                    var search = tempSearches[j];

                    var senable = search.Enable.Value;
                    ImGui.Checkbox($"Enable##search{i}{j}", ref senable);
                    search.Enable.Value = senable;

                    var league = search.League.Value;
                    ImGui.InputText($"League##search{i}{j}", ref league, 100);
                    search.League.Value = league;

                    var searchId = search.SearchId.Value;
                    ImGui.InputText($"Search ID##search{i}{j}", ref searchId, 100);
                    search.SearchId.Value = searchId;

                    if (ImGui.Button($"Remove Search##search{i}{j}"))
                    {
                        tempSearches.RemoveAt(j);
                        j--;
                        continue;
                    }
                }
                group.Searches = tempSearches;

                ImGui.Unindent();
                ImGui.Separator();
            }

            _parent.Groups = tempGroups;

            var newGroupName = _parent.NewGroupName.Value;
            ImGui.InputText("New Group Name", ref newGroupName, 100);
            _parent.NewGroupName.Value = newGroupName;

            if (ImGui.Button("Add Group"))
            {
                _parent.Groups.Add(new SearchGroup { Name = new TextNode(newGroupName), Enable = new ToggleNode(true), Searches = new List<LiveSearchInstanceSettings>() });
            }
        }
    }

    [IgnoreMenu]
    public TextNode NewGroupName { get; set; } = new TextNode("New Group");
}

public class SearchGroup
{
    public TextNode Name { get; set; } = new TextNode("New Group");
    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    public List<LiveSearchInstanceSettings> Searches { get; set; } = new List<LiveSearchInstanceSettings>();
}

public class LiveSearchInstanceSettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    public TextNode League { get; set; } = new TextNode("Rise of the Abyssal");
    public TextNode SearchId { get; set; } = new TextNode("");
}