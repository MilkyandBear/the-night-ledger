using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace SparkPost;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;
    public int MinMinutes { get; set; } = 25;
    public int MaxMinutes { get; set; } = 50;

    // The last successfully fetched message pool, kept so the post still
    // arrives on a night when the network (or GitHub) is being difficult.
    public List<string> CachedMessages { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
