using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace JewYourItem.Models;

public class WsResponse
{
    [JsonProperty("new")]
    public string[] New { get; set; }
}

public class ItemFetchResponse
{
    [JsonProperty("result")]
    public ResultItem[] Result { get; set; }
}

public class ResultItem
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("listing")] public Listing Listing { get; set; }
    [JsonProperty("item")] public Item Item { get; set; }
}

public class Listing
{
    [JsonProperty("method")] public string Method { get; set; }
    [JsonProperty("indexed")] public string Indexed { get; set; }
    [JsonProperty("stash")] public Stash Stash { get; set; }
    [JsonProperty("price")] public Price Price { get; set; }
    [JsonProperty("fee")] public int Fee { get; set; }
    [JsonProperty("account")] public Account Account { get; set; }
    [JsonProperty("hideout_token")] public string HideoutToken { get; set; }
}

public class Stash
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("x")] public int X { get; set; }
    [JsonProperty("y")] public int Y { get; set; }
}

public class Account
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("online")] public object Online { get; set; }
}

public class Price
{
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("amount")] public int Amount { get; set; }
    [JsonProperty("currency")] public string Currency { get; set; }
}

public class Item
{
    [JsonProperty("realm")] public string Realm { get; set; }
    [JsonProperty("verified")] public bool Verified { get; set; }
    [JsonProperty("w")] public int W { get; set; }
    [JsonProperty("h")] public int H { get; set; }
    [JsonProperty("icon")] public string Icon { get; set; }
    [JsonProperty("league")] public string League { get; set; }
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("sockets")] public List<Socket> Sockets { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("typeLine")] public string TypeLine { get; set; }
    [JsonProperty("baseType")] public string BaseType { get; set; }
    [JsonProperty("rarity")] public string Rarity { get; set; }
    [JsonProperty("ilvl")] public int Ilvl { get; set; }
    [JsonProperty("identified")] public bool Identified { get; set; }
    [JsonProperty("note")] public string Note { get; set; }
    [JsonProperty("corrupted")] public bool Corrupted { get; set; }
    [JsonProperty("properties")] public List<Property> Properties { get; set; }
    [JsonProperty("requirements")] public List<Requirement> Requirements { get; set; }
    [JsonProperty("runeMods")] public List<string> RuneMods { get; set; }
    [JsonProperty("implicitMods")] public List<string> ImplicitMods { get; set; }
    [JsonProperty("explicitMods")] public List<string> ExplicitMods { get; set; }
    [JsonProperty("desecratedMods")] public List<string> DesecratedMods { get; set; }
    [JsonProperty("desecrated")] public bool Desecrated { get; set; }
    [JsonProperty("frameType")] public int FrameType { get; set; }
    [JsonProperty("socketedItems")] public List<SocketedItem> SocketedItems { get; set; }
    [JsonProperty("extended")] public object Extended { get; set; }
}

public class Socket
{
    [JsonProperty("group")] public int Group { get; set; }
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("item")] public string Item { get; set; }
}

public class Property
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("values")] public List<List<object>> Values { get; set; }
    [JsonProperty("displayMode")] public int DisplayMode { get; set; }
    [JsonProperty("type")] public int Type { get; set; }
}

public class Requirement
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("values")] public List<List<object>> Values { get; set; }
    [JsonProperty("displayMode")] public int DisplayMode { get; set; }
    [JsonProperty("type")] public int Type { get; set; }
}

public class SocketedItem
{
    [JsonProperty("realm")] public string Realm { get; set; }
    [JsonProperty("verified")] public bool Verified { get; set; }
    [JsonProperty("w")] public int W { get; set; }
    [JsonProperty("h")] public int H { get; set; }
    [JsonProperty("icon")] public string Icon { get; set; }
    [JsonProperty("stackSize")] public int StackSize { get; set; }
    [JsonProperty("maxStackSize")] public int MaxStackSize { get; set; }
    [JsonProperty("league")] public string League { get; set; }
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("typeLine")] public string TypeLine { get; set; }
    [JsonProperty("baseType")] public string BaseType { get; set; }
    [JsonProperty("ilvl")] public int Ilvl { get; set; }
    [JsonProperty("identified")] public bool Identified { get; set; }
    [JsonProperty("properties")] public List<Property> Properties { get; set; }
    [JsonProperty("requirements")] public List<Requirement> Requirements { get; set; }
    [JsonProperty("explicitMods")] public List<string> ExplicitMods { get; set; }
    [JsonProperty("descrText")] public string DescrText { get; set; }
    [JsonProperty("frameType")] public int FrameType { get; set; }
    [JsonProperty("socket")] public int Socket { get; set; }
}

public class Extended
{
    [JsonProperty("dps")] public float Dps { get; set; }
    [JsonProperty("pdps")] public float Pdps { get; set; }
    [JsonProperty("edps")] public float Edps { get; set; }
    [JsonProperty("mods")] public Mods Mods { get; set; }
    [JsonProperty("hashes")] public Hashes Hashes { get; set; }
}

public class Mods
{
    [JsonProperty("explicit")] public List<ExplicitMod> Explicit { get; set; }
    [JsonProperty("implicit")] public List<ImplicitMod> Implicit { get; set; }
    [JsonProperty("desecrated")] public List<DesecratedMod> Desecrated { get; set; }
}

public class ExplicitMod
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("tier")] public string Tier { get; set; }
    [JsonProperty("level")] public int Level { get; set; }
    [JsonProperty("magnitudes")] public List<Magnitude> Magnitudes { get; set; }
}

public class ImplicitMod
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("tier")] public string Tier { get; set; }
    [JsonProperty("level")] public int Level { get; set; }
    [JsonProperty("magnitudes")] public List<Magnitude> Magnitudes { get; set; }
}

public class DesecratedMod
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("tier")] public string Tier { get; set; }
    [JsonProperty("level")] public int Level { get; set; }
    [JsonProperty("magnitudes")] public List<Magnitude> Magnitudes { get; set; }
}

public class Magnitude
{
    [JsonProperty("hash")] public string Hash { get; set; }
    [JsonProperty("min")] public string Min { get; set; }
    [JsonProperty("max")] public string Max { get; set; }
}

public class Hashes
{
    [JsonProperty("explicit")] public List<List<object>> Explicit { get; set; }
    [JsonProperty("implicit")] public List<List<object>> Implicit { get; set; }
    [JsonProperty("rune")] public List<List<object>> Rune { get; set; }
    [JsonProperty("desecrated")] public List<List<object>> Desecrated { get; set; }
}
