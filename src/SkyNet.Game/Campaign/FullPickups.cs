namespace SkyNet.Game.Campaign;

/// <summary>
/// FULL PICKUPS (issue #25). TAKE consumes every item as DOS does, even when it gives nothing;
/// LEAVE leaves health, armour and ammo on the ground while that stat is full. A weapon is always
/// taken: guns share pools, so a full pool must not keep a new gun out of reach.
/// </summary>
public enum FullPickups { Take, Leave }
