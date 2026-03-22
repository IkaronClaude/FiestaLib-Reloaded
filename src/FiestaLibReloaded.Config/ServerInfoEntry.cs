namespace FiestaLibReloaded.Config;

public record ServerInfoEntry(
    string Name,
    int ServerType,
    int WorldNum,
    int ZoneNum,
    int FromServerType,
    string IpAddress,
    int Port,
    int MaxConnections,
    int UserLimit);
