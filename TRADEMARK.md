# Trademark and brand policy

The code is [Apache-2.0](LICENSE). The name is not, and the two are separate
things: a copyright licence says what you may do with the source, and it has
nothing to say about what you may call the result.

**"RoseMCP" and "BinaryVibrance", the rose mark, and the icon and image files
under `src/RoseMcp.Tray/Assets/` and `src/RoseMcp.Inspector/Assets/` are
trademarks and brand assets of Steven Blom, trading as BinaryVibrance.** Section
6 of the Apache License grants no rights in them, and that exclusion is
deliberate rather than an oversight.

Fork it. Build it. Ship it to whoever you like. The one thing asked is that what
you ship is recognisably yours, so that somebody who has a problem with your
build knows whose door to knock on.

## What needs no permission

None of this needs asking, and none of it is affected by the policy below:

- Saying your project is a fork of RoseMCP, is based on it, or is compatible
  with it. Describing where something came from is ordinary and fair.
- Using the name in documentation, articles, talks, comparisons, reviews or
  issue reports.
- Naming it in a dependency list, a package manifest, a changelog or a commit
  message.
- Running a modified build yourself, inside your company, under any name you
  like. This policy is about what you *distribute*.
- Quoting the marks where the Apache License itself requires it: describing the
  origin of the work, and reproducing [NOTICE](NOTICE).

## What a distributed fork should do

If you publish a build that is not from this repository:

- **Give it its own name.** Not "RoseMCP", and not a name whose whole point is
  that it reads as RoseMCP. "RoseMCP Plus", "RoseMCP-ng" and "Rose MCP" are the
  kind of thing being asked about here; something of your own is not.
- **Give it its own icons.** Replace the files named above. They are the mark in
  picture form, which is why they are excluded from the licence grant even
  though they are files in the repository like any other.
- **Do not imply it is official**, endorsed, supported or affiliated.
- **Change the install identity** if you ship an installer, so yours and this one
  can coexist on a machine: the `AppId` in
  [installer/rosemcp.iss](installer/rosemcp.iss), the install root, the
  Add/Remove Programs `DisplayName` and `Publisher`, and the `RoseMCP` value the
  tray writes under the `Run` key. Two products sharing those uninstall each
  other, which is a worse outcome for your users than for anybody else.

## Official builds

The only official distribution of RoseMCP is
**https://github.com/AtomicBlom/RoseMCP/releases**.

A build from anywhere else is somebody else's, whatever it is called. If you
find one presented as official, please open an issue.

## Asking

If you want to do something this policy does not obviously allow, ask. The
intent is to stop confusion about who made what, not to make the project awkward
to work with. Open an issue and say what you have in mind.

This policy may change; it does not apply retroactively to anything already
distributed in good faith under an earlier version of it.
