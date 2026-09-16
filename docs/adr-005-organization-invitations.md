# ADR 005 — Membership is offered, not granted

**Status:** accepted · **Date:** 2026-09-16 · **Supersedes:** nothing ·
**Related:** [permissions-model.md](permissions-model.md), [ADR 004](adr-004-profile-pictures.md)

## What was broken

`POST /orgs/{orgId}/members` took a **user id** and put that person into the organization
immediately. Three consequences, none of them intended:

1. **No consent.** An administrator could add anyone with an account to their organization without
   telling them. The person found out by seeing it in their workspace.
2. **No reach.** The client had to resolve an address through `GET /users/by-email` first, and that
   answers 404 for somebody who has not signed up — so the one case an invite flow exists for, a
   colleague who is not on BoardSync yet, could not be expressed at all.
3. **A misleading button.** The UI called it "Invite Member" and sent no email.

## The decision

An **invitation**, keyed on an email address rather than a user id — because at the moment it is
created there may be no user to key on, which is precisely the case the old flow could not reach.

| | |
| --- | --- |
| `POST /orgs/{orgId}/invitations` | Create and email one. `org:member:manage`. |
| `GET /orgs/{orgId}/invitations` | Who is outstanding. `org:member:manage`. |
| `DELETE /orgs/{orgId}/invitations/{id}` | Withdraw. `org:member:manage`. |
| `GET /invitations/{token}` | What the link offers. **Anonymous.** |
| `POST /invitations/{token}/accept` | Join. Signed in, as the invited address. |

The last two live on their own controller because they are addressed by **token**, not by
organization: the holder does not know which organization they are joining until it is read, so
there is no `orgId` for `[RequirePermission(..., From = "orgId")]` to bind to.

## The rule the whole thing rests on

**The account accepting must own the address the invitation was sent to.**

A token in an email is a bearer credential — forwarded, quoted in a reply, or read off a shared
screen, it reaches people it was not addressed to. Binding acceptance to the address means holding
the link is not enough: you must also control the mailbox, which is the thing the administrator
actually vouched for. Verified end to end: a second signed-in account presented with a valid link
is refused, and the organization stays unreadable to them.

The token itself is stored only as a SHA-256 hash, the same treatment password-reset and
email-confirmation tokens already get. A database read is then not a pile of working invitations
into every organization.

## The preview is anonymous, and thin

It has to be anonymous: the recipient may have no account, and the page cannot choose between
"sign in" and "create an account" until it knows which. It returns the organization's name, the
invited address, the role, the inviter's display name, and whether that address has an account —
and nothing else. A link that reaches the wrong inbox should not become a readout of an
organization's membership.

## An invitation proves the address

`RequireEmailConfirmation` is on, but a new account created through an invitation is active
immediately and gets no confirmation email. Reading the invitation *is* the proof confirmation asks
for: it was sent to that address, and only somebody who can read that mailbox has the token.
Requiring both establishes the same fact twice and puts another step between accepting an
invitation and being in the organization.

A token that is absent, expired, revoked, spent, or addressed to somebody else simply does not
count — registration continues down the ordinary confirmation path rather than failing, because a
bad token is no reason to refuse somebody an account.

## Smaller decisions

**Re-inviting supersedes.** The previous open invitation for that address is revoked as the new one
is written. Two live links to one membership would mean revoking the one an administrator can see
leaves the other working.

**Status is derived, never stored** — from `AcceptedAt`, `RevokedAt` and `ExpiresAt` — so it cannot
disagree with its own timestamps.

**The role is validated against `RolePermissions.AssignableAt(Organization)`**, not against the
whole enum. Otherwise the invitation becomes the back door for a grant the role-change endpoint
already refuses: `ProjectAdmin` at organization scope grants nothing and reads as though it grants
everything.

**Revoking stops at acceptance.** Once somebody has joined, revoking would claim they are not a
member while they are. Removing a member is a different act, with its own endpoint and its own
protection for the last OrgAdmin.

**The email that could not be sent is still recorded.** The invitation row survives a failed send
and the endpoint reports the failure, so an administrator can send it again — deleting it would
also undo the supersede above and quietly revive the link it replaced.

## What this cost elsewhere

`POST /orgs/{orgId}/members` is gone, and one integration-test helper used it — which cascaded to
33 tests. The helper now seeds an invitation row and calls the real accept endpoint, so every test
needing an organization member exercises the new path, and its email-match rule, on the way past.

The test host also gained a stubbed `IEmailService`. Every email the API sent under test used to be
a real SMTP attempt that failed slowly and silently; that was tolerable only while nothing depended
on the result.

## Not done

**Team and project invitations.** Both still add a known user directly. They are a smaller problem
— you are already inside the organization to be added to one of its teams — but the same shape
would fit.

**Resend as its own endpoint.** Re-inviting the same address supersedes and re-sends, which covers
it; a dedicated endpoint would be a second way to do one thing.
