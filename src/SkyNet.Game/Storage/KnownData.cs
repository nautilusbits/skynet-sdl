using SkyNet.Formats;

namespace SkyNet.Game.Storage;

/// <summary>
/// The data releases the port is known to play right, per game: each release's MDMD*.BSA
/// archives (<see cref="DataVersions"/>). A player's unknown release is added here from the
/// sizes and hashes the log lists.
/// </summary>
internal static class KnownData
{
    private static readonly IReadOnlyList<DataFile>[] SkyNet =
    [
        [
            new("MDMDBRIF.BSA", 125933, "da4cc5c4f30ae2305a36182cfa24a2a402d538707463dfd3d0ae2d907089e7b1"),
            new("MDMDENMS.BSA", 3600592, "02165fcd14d323180ab00eb846747782680b096bde2e6944eac3b2fead2f66fc"),
            new("MDMDHRES.BSA", 2530340, "b6c3f625d20338e19412fcfafdf42d0b5e9e059bb147fd900cff8ad810cbc520"),
            new("MDMDIMGS.BSA", 5424890, "ebb7d833ff55911cdf648ab5c74a9649907e694acf4fbd4551f468dd6c779e2f"),
            new("MDMDMAP2.BSA", 3242716, "95dba9ad59ce697e58b41f106d72cd5293ffd3a48dcc1702716a206e4a603e17"),
            new("MDMDMUSC.BSA", 384626, "58842533d67d2b7e4a9ea48a7afa42e913f1c630a3e9c5c8b1f5285746cd4f29"),
            new("MDMDOBJS.BSA", 13274384, "24969a1d5792f7dfd61930c8d110fe6f268c8cd4ce33f3e8e799bcf6676f36e5"),
            new("MDMDSFXS.BSA", 6346453, "71ef504d17013f677d6220ec23a6172264ba30a96429da1941cdee8db3a44b42"),
        ],
    ];

    private static readonly IReadOnlyList<DataFile>[] FutureShock =
    [
        [
            new("MDMDBRIF.BSA", 171857, "1ace67ac45bf487c78c62826cc8f87573b3df54f28306613fcb55fa35c6d3a44"),
            new("MDMDENMS.BSA", 3536596, "7982cc1ef3e8dc293abcadeadeceef77b62207189e36ccab7a30733cc7b44356"),
            new("MDMDIMGS.BSA", 3651807, "c056becbcb5922f503c2f240b797bdf1b68a23d0a89afae122c744d5d50786ce"),
            new("MDMDMAPS.BSA", 6564216, "33c3061ab3342fe9bc6249ed1dda257a2d246016d4f3863a106d7443dfbf3dc3"),
            new("MDMDMUSC.BSA", 384500, "77212be2468289369fcadf1bad55c3cb727c6e501e0398408de0dd56837ac835"),
            new("MDMDOBJS.BSA", 5924896, "e7534e0076d2b39243009f9eaffc24f54f9b90a6d1e4e09495ffd06332232254"),
            new("MDMDSFXS.BSA", 2713079, "673d072f75c010c8aa6f5d999fb48623b6fdd2eea9de6db8ef49445465f57ab9"),
        ],
    ];

    /// <summary><paramref name="game"/>'s known releases; none for <see cref="GameKind.None"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<DataFile>> Of(GameKind game) => game switch
    {
        GameKind.SkyNet => SkyNet,
        GameKind.FutureShock => FutureShock,
        _ => [],
    };
}
