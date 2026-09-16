# ADR 004 — Avatars upload straight to blob storage

**Status:** accepted · **Date:** 2026-09-08 · **Supersedes:** nothing ·
**Related:** [ADR 003](adr-003-prd-destination.md), [permissions-model.md](permissions-model.md)

## What was broken

`User.ProfilePictureUrl` has existed since the first migration. Every member list, the org
overview, and the task card already read it. Nothing could ever write it.

Four separate defects, each quiet:

1. **Saving a name deleted the picture.** `UserService.UpdateAsync` read
   `request.ProfilePictureUrl ?? string.Empty`, and the profile form does not send that field — so
   every rename cleared the avatar. Nobody noticed because nobody could set one in the first place.
2. **The only way to set it was to assert a URL.** `UpdateProfileRequest.ProfilePictureUrl` was
   validated as `[Url]` and nothing else, so a profile picture could be pointed at any host —
   a request-forgery and tracking surface dressed as a settings field.
3. **The user menu never rendered it.** It drew initials unconditionally, in the one place someone
   looks to confirm who they are signed in as.
4. **Board cards could not render it.** `boardMapper` built every assignee as
   `{ id, name: "Unknown User" }`, and the one place that later resolved the real name copied the
   name across and dropped the picture — so an avatar the API had been returning all along reached
   nothing.

`""` versus `null` was the fifth. The column is non-nullable and holds `""` for a user who has
never set a picture, while `UserProfile.ProfilePictureUrl` is declared nullable and every client
tests it for null. It worked only because `""` is falsy in JavaScript.

## The decision

**The browser uploads to blob storage directly, using a URL the API signs. The bytes never pass
through the API.** Azure Blob Storage, with Azurite in development.

Three endpoints, all on the caller's own profile and all exempt from permission checks because the
subject is the token:

| | |
| --- | --- |
| `POST /Auth/profile/picture/upload-url` | Signs a service SAS for one blob, valid ten minutes |
| `POST /Auth/profile/picture` | Verifies what landed, stores the URL, deletes the previous blob |
| `DELETE /Auth/profile/picture` | Clears the field and the blob |

The blob path is `{userId}/{guid}.{ext}`, **built from the token's subject and never from the
request**. That is the entire safety argument for handing a browser a write capability: the SAS is
scoped to one blob (`sr=b`, `sp=cw`), and that blob is under a prefix only this user's tickets are
ever issued for. The commit step re-checks the prefix, because the client hands the path back.

## The cost, and how it is paid

Direct upload means that between signing a URL and the client calling back, **the server sees
nothing**. A SAS constrains who may write and for how long; it cannot constrain *what*. The
declared content type is a claim, and it is written onto the blob verbatim.

So validation happens twice, and the second time is the one that counts:

- **On the ticket** — content type against an allowlist, size against the limit. Fails fast, gives
  a useful message, trusts the client.
- **On commit** — the API reads the blob's first sixteen bytes back out of its own container and
  identifies the format from the file's own signature. A range read, not a download, so a caller
  cannot make the API stream an arbitrary blob by pointing a commit at one.

Anything that fails is **rejected and deleted**. Leaving it would mean the one place a user can
write to accumulates whatever they chose to put there — a small file drop, dressed as a profile
page. Verified end to end: an HTML document uploaded through a ticket issued for `image/png` is
accepted by storage, refused on commit, and gone from the container.

**SVG is excluded from the allowlist rather than sanitised.** It is a document that can carry
script, and serving one as an image every teammate's browser loads is a stored-XSS shape.

## Container is public-read

Avatars are shown in `<img>` tags to everyone who can see the member list. The alternative — a read
SAS minted per view — puts a short expiry on a URL that gets cached, embedded in a rendered page,
and read again later. Nothing in the container is a secret, and the path is an unguessable GUID.

The GUID also means **a replaced picture is a new URL**, never the same one with new content, so a
cached copy in a browser or CDN can never show the previous image. That is what lets the blobs be
served `immutable, max-age=31536000`.

## Ordering, on replacement

The profile is committed first, and only then is the old blob deleted. Deleting first would, on a
failed save, leave the user pointing at a URL that 404s — a working avatar traded for a broken one.
Delete failures are logged and swallowed: the profile is correct either way, and the worst outcome
is an orphaned blob.

## Optional, like the model providers

With no `Storage:ConnectionString` the client reports itself unconfigured, the two upload endpoints
answer **503** — not 500, because it is a configuration choice and the client's correct response is
to hide the control — and everything else works unchanged, including avatars set before now, whose
URLs live on the user row. `DELETE` works regardless: somebody who wants their picture gone should
not be told to wait for an integration.

Container creation and CORS rules are written lazily on first use rather than at startup, so
unreachable storage costs one failed upload rather than an API that will not boot.

## CORS is not optional

The browser PUTs to the storage endpoint, which is cross-origin. **Without rules on the storage
account every upload fails preflight**, with no server-side trace. `Storage:ConfigureCors` writes
them from the app's own `AllowedOrigins`; it is on in development because Azurite starts with none,
and off elsewhere because it lets the application rewrite account-wide settings. A real deployment
configures the account once, out of band.

## What was not done

**Managed identity.** The connection string must carry an account key, because a service SAS can
only be signed with a shared key. Moving to managed identity means a user-delegation SAS; nothing
above `IAvatarStorage` changes.

**Image processing.** No resize, no re-encode, no strip of EXIF. A 5MB original is served to a
48px circle, and location metadata in an uploaded photo survives. Re-encoding server-side would
also settle the format question by construction rather than by allowlist. It needs the bytes,
which is exactly what this design routes around — the natural home is a job triggered on commit,
not the request path.

**A general file-storage abstraction.** `IAvatarStorage` knows about avatars. The paths, the
public-read container and the short SAS lifetime only make sense for small public images; an upload
that is not one should get its own interface rather than widen this.

## Organizations, added the same day

An organization's logo had the identical defect — an `[Url]`-validated `avatarUrl` string on
`PUT /orgs/{orgId}`, editable as a "paste a link" box — and worse consequences, because every
member's browser fetches an organization's logo, so that field aimed a request from all of them at
whatever an administrator pasted.

It is the same object: a small public image, same formats, same ceiling, same container. So the
storage is shared and the two separate at the **prefix**, via `AvatarOwner`:

| Owner | Prefix | Authorized by |
| --- | --- | --- |
| User | `users/{userId}` | being the token's subject |
| Organization | `organizations/{orgId}` | holding `org:admin` on that organization |

Kind-first, so the namespaces cannot collide even in the impossible case of a user id equalling an
organization id — a commit is checked against the prefix, so an `org:admin` ticket cannot address a
person's avatar however the ids fall.

Everything between — the allowlist, the size ceiling, reading the bytes back, deleting what fails —
is `AvatarUploadPipeline`, shared by both services. That is not tidiness: the verification is the
only thing standing between a signed upload URL and an arbitrary file served from the app's own
storage, and a second copy of it is a second thing that can quietly fall behind the first.

`UpdateOrganizationRequest.AvatarUrl` is gone, as `UpdateProfileRequest.ProfilePictureUrl` was.
Setting the logo goes through `POST /orgs/{orgId}/avatar/upload-url` → `POST /orgs/{orgId}/avatar`,
and clearing it through `DELETE`. Both still emit `OrganizationUpdated`, so the change stays in the
activity feed.

### The feed records that the logo changed, not what it changed to

The feed renders `Field: before → after`, and the first version of this passed the blob URLs
straight in. That produced a 280-character line of two near-identical GUID paths as an entry's
one-line description — and the "before" half was a **dead link by the time anyone read it**, since
committing a new logo deletes the blob it replaced.

So the slot carries what happened: `Logo: updated`, `Logo: removed`. Nothing is lost by it. An
avatar has no readable value to report the way a name or a description does, and the picture itself
is on the organization for anyone who wants to look. It is also what the membership events already
do — they resolve a user id to a name rather than logging the id.

A user's profile picture emits no activity event at all, and shouldn't: the feed is organization,
project and team activity, and what somebody sets as their own picture is not that.

The rows written before that change keep their URLs until something rewrites them, and a feed is
read as history — the old entries are the ones anybody scrolling back actually sees. Migration
`20260916090000_TidyAvatarActivityDetail` rewrites them into the same shape. Which case each row
was is recoverable without the URLs: a removal left the new value null, and everything else set
one. Its `Down` is empty on purpose — the URLs are unrecoverable, and every one of them named a
blob that has already been deleted.

**Existing user avatars keep working.** They sit at `{userId}/…` with no kind segment. Nothing
reads them back by prefix — the stored URL is absolute, and deleting a replaced one goes through
the container-relative path — so the older layout resolves as before and is simply never written
again.
