namespace WinUsage
{
    /// <summary>
    /// Sprite 16x16 delle notifiche del controllo remoto (disegnati a mano, tavolozza di <see cref="Sprites.Palette"/>).
    /// Si disegnano raddoppiati, quindi occupano 32x32 pixel della scena come l'elmo del messaggio di soglia.
    /// </summary>
    internal static class RemoteSprites
    {
        public static readonly string[] Camera =
        {
            "................",
            "....kkkk........",
            "...kBBBBk...kkk.",
            ".kkkkkkkkkkkkrrk",
            "kyyyyyyyyyyyyyyk",
            "kyyyykkkkkyyyyyk",
            "kyyykaaaaakyyyyk",
            "kyykagguggakyyyk",
            "kyykaguWugakyyyk",
            "kyykagguggakyyyk",
            "kyyykaaaaakyyyyk",
            "kyyyykkkkkyyyyyk",
            "kBBBBBBBBBBBBBBk",
            ".kkkkkkkkkkkkkk.",
            "................",
            "................",
        };

        public static readonly string[] Periscope =
        {
            "................",
            "...kkkkkkkk.....",
            "..kaggggggkk....",
            "..kaggggggeuk...",
            "..kaggggggkk....",
            "...kkkagkkk.....",
            ".....kagk.......",
            ".....kagk.......",
            ".....kagk.......",
            "uuuuukagkuuuuuuu",
            "cccc.kagk.cccc..",
            ".....kagk.......",
            ".....kagk.......",
            ".....kagk.......",
            ".....kkkk.......",
            "................",
        };

        public static readonly string[] Shell =
        {
            "................",
            "................",
            ".....kkkkkk.....",
            "...kkFfFFfFkk...",
            "..kFfFFfFFfFfk..",
            ".kFfFFfFFfFFfFk.",
            ".kfFFfFFfFFfFFk.",
            "kFfFFfFFfFFfFFfk",
            "kfFFfFFfFFfFFfFk",
            "kFFfFFfFFfFFfFFk",
            ".kkFfFFfFFfFFkk.",
            "...kkFFfFFfkk...",
            ".....kkFFkk.....",
            "....kFFkkFFk....",
            ".....kkkkkk.....",
            "................",
        };

        public static readonly string[] Padlock =
        {
            "................",
            ".....kkkkkk.....",
            "....kuuuuuuk....",
            "...kuk....kuk...",
            "...kuk....kuk...",
            "...kuk....kuk...",
            ".kkkkkkkkkkkkkk.",
            ".kyyyyyyyyyyyyk.",
            ".kyyyyykkyyyyyk.",
            ".kyyyykddkyyyyk.",
            ".kyyyyykdkyyyyk.",
            ".kyyyyykdkyyyyk.",
            ".kBBBBBBBBBBBBk.",
            ".kkkkkkkkkkkkkk.",
            "................",
            "................",
        };

        public static readonly string[] Siren =
        {
            "................",
            "................",
            "......kkkk......",
            ".....krrrrk.....",
            "....krreerrk....",
            "....krreerrk....",
            "...krrrrrrrrk...",
            "...krrrrrrrrk...",
            "...krrrrrrrrk...",
            "..kkkkkkkkkkkk..",
            "..kaaaaaaaaaak..",
            "..kkkkkkkkkkkk..",
            "................",
            "................",
            "................",
            "................",
        };
    }
}
