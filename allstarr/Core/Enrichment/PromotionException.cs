namespace allstarr.Core.Enrichment;

public enum PromotionFailure
{
    LibraryFolderNotSet,
    LibraryFolderUnavailable,
    LibraryFolderFull,
    LibraryFolderMismatch,
    NamingPatternNotSelected,
    NamingMetadataMissing,
    ReleaseMetadataUnavailable,
    DestinationChoiceRequired,
    ExistingAlbumLayoutMismatch,
    SourceChanged,
    DestinationExists,
    PromotionChanged,
    NotAuthorized,
    InvalidRequest
}

public sealed class PromotionException(PromotionFailure failure) : InvalidOperationException(MessageFor(failure))
{
    public PromotionFailure Failure { get; } = failure;
    public string Reason => Failure switch
    {
        PromotionFailure.LibraryFolderNotSet => "library-folder-not-set",
        PromotionFailure.LibraryFolderUnavailable => "library-folder-unavailable",
        PromotionFailure.LibraryFolderFull => "library-folder-full",
        PromotionFailure.LibraryFolderMismatch => "library-folder-mismatch",
        PromotionFailure.NamingPatternNotSelected => "naming-pattern-not-selected",
        PromotionFailure.NamingMetadataMissing => "naming-metadata-missing",
        PromotionFailure.ReleaseMetadataUnavailable => "release-metadata-unavailable",
        PromotionFailure.DestinationChoiceRequired => "destination-choice-required",
        PromotionFailure.ExistingAlbumLayoutMismatch => "existing-album-layout-mismatch",
        PromotionFailure.SourceChanged => "source-changed",
        PromotionFailure.DestinationExists => "destination-exists",
        PromotionFailure.PromotionChanged => "promotion-changed",
        PromotionFailure.NotAuthorized => "not-authorized",
        _ => "invalid-request"
    };
    public int Status => Failure switch
    {
        PromotionFailure.LibraryFolderNotSet or PromotionFailure.LibraryFolderUnavailable or PromotionFailure.LibraryFolderFull => 503,
        PromotionFailure.NotAuthorized => 403,
        PromotionFailure.LibraryFolderMismatch => 400,
        PromotionFailure.InvalidRequest => 400,
        _ => 409
    };

    private static string MessageFor(PromotionFailure failure) => failure switch
    {
        PromotionFailure.LibraryFolderNotSet => "Adding to your library isn't set up. Choose where added songs go in library settings. Allstarr needs write access to that folder.",
        PromotionFailure.LibraryFolderUnavailable => "The approved library folder is unavailable. Ask an administrator to check its mount and write access.",
        PromotionFailure.LibraryFolderFull => "The library drive is almost full, so nothing was added. Free up space on it, then try again.",
        PromotionFailure.LibraryFolderMismatch => "This folder is not mapped to the selected Jellyfin library. Ask an administrator to select its music folder again.",
        PromotionFailure.NamingPatternNotSelected => "Choose and confirm a naming pattern in library settings before adding songs.",
        PromotionFailure.DestinationChoiceRequired => "An existing album folder differs from the proposed destination. Open the song preview and choose which folder to use before adding it.",
        PromotionFailure.ExistingAlbumLayoutMismatch => "This album already has a folder that the selected naming pattern cannot reproduce. Choose a matching naming pattern in library settings before adding songs.",
        PromotionFailure.ReleaseMetadataUnavailable => "The selected release cannot be matched to this song with complete, unambiguous metadata. Choose a verified release before adding it to your library.",
        PromotionFailure.NamingMetadataMissing => "This song is missing a year or track number used by your naming pattern. Choose a release with complete metadata, or explicitly choose a naming pattern without those fields in library settings. Nothing was added.",
        PromotionFailure.SourceChanged => "The source copy changed or is no longer available. Refresh and choose a verified complete copy.",
        PromotionFailure.DestinationExists => "A file already exists at the destination. Review it before trying again.",
        PromotionFailure.PromotionChanged => "The promotion changed. Review its current status before trying again.",
        PromotionFailure.NotAuthorized => "Your account isn't authorized to add to this library.",
        _ => "This promotion request is invalid. Review the song, folder and requested action."
    };
}
