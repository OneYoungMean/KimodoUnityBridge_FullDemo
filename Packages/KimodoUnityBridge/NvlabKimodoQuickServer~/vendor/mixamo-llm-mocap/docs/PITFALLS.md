# Pitfalls

Every one of these was paid for in real passes across three model
generations working on this rig. If you are an AI operating this
pipeline: read this file before your first pass and again before
touching a clip a human has partially signed off.

## Space and measurement

1. **Blender IK explodes this rig.** `POSE_IK` on the legs sent feet to
   −81 m. The skeleton is FK-only; plant feet via hip-height search +
   Z-only flattening. If FK plant looks wrong, fix the spec or the
   lift — never "just add IK for the landing".
2. **World-space aim is 100× off.** Composing a pose matrix through
   `arm.matrix_world` (scale 0.01) and assigning it sends hands to
   z ≈ 10 m. Aim in armature centimeters (`aim_bone` does).
3. **`pb.head` lies on a posed rig.** Only
   `(arm.matrix_world @ pb.matrix).to_translation()` after a depsgraph
   update is truth.
4. **Stale location keys survive rewrites.** A failed pass that keyed
   locations on non-hip bones will haunt a rotations-only rewrite.
   `apply_mixamo_fk` deletes and rebuilds the action and re-keys zero
   locations everywhere for this reason.

## Reading the video

5. **Never decide a limb from a still.** Facing the camera,
   viewer-left = character-RIGHT. Three separate incidents: a kick
   built on the wrong leg (full wasted pass), a "left knee" jump that
   was numerically right-knee, a "chambered fist" that was a
   foreshortened punch toward the camera. `analyze_landmarks.py` output
   is the only admissible evidence for the beat sheet.
6. **Gen-video does not follow its prompt.** Prompted left-knee jump →
   right knee; prompted extended front kick → high-knee snap; prompted
   A-pose → the model's own idea of one. Retarget what was filmed;
   never author toward the prompt.
7. **The estimator loses occluded limbs.** An arm behind the torso in
   3/4 view came back as garbage (lateral-back instead of chambered at
   the chest). When a human's read of the video contradicts the
   estimator, the human is right — encode it as a spec `arm_override`
   with ramps, and leave the estimator's other limbs alone.

## Feet and ground

8. **Per-frame hip-centered landmarks make planted feet skate.** The
   support foot wandered 0.30 m during a kick. Fix is structural (the
   lift pins the support ankle's XZ and translates the whole pose;
   pelvis sways over the foot, which is correct physics) — do not
   patch it per-frame in the apply.
9. **A degenerate aim target bends toes randomly.** Aiming ToeBase at
   its own head position is a near-zero-length target. Toe aims need a
   point several cm FORWARD along the foot's own heading.
10. **Foot heading follows the foot/hips, never a world axis.**
    "Straightening" toes toward world −Y on a yawed stance turns them
    outward on screen. Keep estimator XZ; constrain heights only.
11. **Never snap the acting limb to the floor.** A floor snap that kept
    running while a kick lifted pinned the foot for one frame and made
    the next frame pop. The plant schedule exists so ground logic
    knows whose foot is acting.

## Blender / tooling

12. **Viewport screenshots capture black** when the Blender window is
    occluded or minimized. Stills go through a temporary camera +
    Workbench render (`run_stills_render`), always.
13. **The long apply blocks Blender's UI.** 300 frames ≈ 2–4 minutes of
    frozen window while the socket request executes. Normal; don't
    kill it. Results are also written to disk (`apply_result.json`) so
    a dropped socket loses nothing.
14. **mcp SDK 2.x breaks the official Blender MCP server** (it imports
    `mcp.server.fastmcp`, removed in 2.0). Pin `mcp[cli]<2` when
    registering the server.
15. **Stale stills burn passes.** After every re-key, delete and
    re-render the stills before judging anything. An old PNG has
    cost this project at least two decisions.
16. **The live scene holds ONE action — the last one applied.** A
    preview rendered after working on another clip silently shows the
    wrong animation (a source-vs-retarget showcase then looks
    "desynchronized" when it is in fact a different clip).
    `render_preview.py` binds the spec's action before rendering for
    exactly this reason; anything else that renders the scene must do
    the same.

17. **Head orientation must come from the estimator, or the gaze is a
    lie.** Joint positions cannot describe where a head looks (the head
    joint sits inside the skull), so face landmarks must be read from
    real mesh vertices — synthesizing nose/ears from the torso basis
    makes the ear line *identical* to the shoulder line and locks the
    character's gaze to its chest for the whole clip. Symptoms: the head
    swings wide whenever the body twists, and "head vs chest yaw"
    measures exactly 0.000 at every frame — a number too clean to be
    real. Compounding it: the FK apply aims only the skull axis (+Y), so
    the FACE axis (head local +Z) inherits torso lean and yaw. Fix at
    the source (estimator emits `gaze`), orient the head to it in the
    apply, and let `qa_clip.py` flag a chest-locked head (gaze-vs-chest
    std < 2°) and sky-staring (mean elevation > 12°). Three passes were
    burned "fixing" this downstream before the estimator was suspected.
18. **Smoothing eats strikes.** A `smooth` window wide enough to calm a
    fast flurry (9 frames) also averages away its extension peaks —
    punches visibly shrink. Keep flurry windows narrow (5) and restore
    amplitude with `reach`.
19. **Judge limb height against the HEAD, and check proportions before
    correcting.** The eye reads hands against the face, so a guard that
    measures fine relative to the shoulders can still sit at face level
    on a character whose head/shoulder proportions differ from the
    performer's. Measure both, and split the error: on one clip 4.6 cm
    of a 14.8 cm gap was pure proportion (the character's head sits
    lower on its shoulders) and only the remainder was pose worth
    correcting.
20. **Correct a limb by ROTATING the chain, never by translating its
    joints.** Offsetting elbow/wrist/hand and re-normalizing bone
    lengths is fine for a centimetre or two and destroys the pose at ten:
    the re-normalization drags the hands toward the midline and folds the
    wrists (measured: hand separation 0.25 m -> 0.08 m, wrist bend 7° ->
    123°). A rigid rotation about the shoulder socket cannot distort the
    chain — it preserves elbow bend, wrist alignment and the distance
    between the hands. Express the target in metres (`arm_pose.drop_m` /
    `widen_m`) and solve the angle per frame; a fixed angle over- or
    under-shoots as the arm swings.
21. **A "Hand" bone's world position is the WRIST.** Blender reports a
    bone's head; `mixamorig:*Hand` sits at the wrist, so dividing its
    distance-from-shoulder by (arm+fore+hand) understates extension by
    ~15%. Measure the wrist against arm+fore, or the fingertip bone.

## Process

22. **Beat sheet before any spec.** The 16-pass punch and the 4-pass
    kick differ by exactly this discipline; the two shipped clips took
    1–2 passes each.
23. **One constraint per pass; signed-off regions are frozen.** The
    single historical regression came from "improving" feet while
    polishing something else after the upper body was approved.
24. **Map human notes to the owning system before editing.** "Frames
    18–50, punching side" once meant the *guard*, not the punch — a
    pass was wasted editing the wrong function. Convert the frame range
    to src frames, find the beat, find the spec field; only then edit.
25. **QA passing ≠ done.** Numbers catch explosions, pops, skate and
    drift; they cannot see a wrong silhouette. Play the clip, read the
    stills, ask the human.

## Two characters in one scene

26. **Two performers cannot be compared in the estimator's `world`
    frame.** Each one is normalised so that *their own* frame-0 facing
    becomes forward, so two performers arrive in two frames that differ
    by however much their opening yaws differed. Composing their limbs
    onto a shared root looks reasonable and is quietly wrong: it put a
    kicking foot 0.18 m from the other fighter's skull when the video
    and an independent image-space measurement both said 0.29 m — the
    difference between a clean miss and a foot through a head. Anything
    that measures two people against each other must read the CAMERA
    frame (`incam`), the only frame they actually share.
27. **The camera frame's "up" is not gravity.** Heights read straight
    out of camera space carry the camera's pitch — 4.2° on the duel
    plate, which inflated a head-high kick's peak by 0.14 m and
    manufactured a "the kick is too low" finding that did not exist.
    The estimator emits both a gravity-aligned frame and a camera frame;
    fit the rotation between them and push the world's up axis through
    it, then measure in that.
28. **The world-frame root trajectory drifts; the in-camera one does
    not.** Both describe where the performer stood. Measured on the same
    plate: the gravity-aligned global root under-reported a 0.92 m
    step-in as 0.68 m and left the performer 0.16 m off his mark at the
    closing T-pose, while the in-camera root tracked an independent
    image-space measurement (hip pixels ÷ body pixels) to within 2 cm on
    all 241 frames. Drive `root_motion` from `incam`, and trust only its
    lateral component — depth is the ill-conditioned axis of a
    single-camera fit, and one deep crouch moved it 0.22 m in a frame.
29. **Matched proxies, or the comparison is fiction.** Three separate
    false findings in one session, all from comparing a quantity to a
    different quantity: a performer's *nose* against a character's Head
    bone (which sits at the skull base, ~10 cm behind the face); a
    reference *shoulder line* against `mixamorig:Neck` (5–10 cm higher,
    so the torso capsule reached out and "caught" limbs that missed);
    and limb ENDPOINTS instead of whole segments (a shin can pass
    straight through a skull with both its knee and its toe outside).
    Before believing any cross-domain number, name the anatomical point
    on both sides and check they are the same one.
30. **In-place is a solo assumption.** Mixamo clips are in-place by
    convention and every solo plate here is retargeted that way. Two
    fighters are the opposite case: the distance between them IS the
    scene. On the duel plate the attacker closed 1.95 m → 0.88 m and
    back; in place, every punch lands a metre short of the other
    character. One stage, one scale — the initial separation and both
    performers' travel must share a scale factor or the geometry never
    closes.
31. **Flattening the toe direction is right for a planted foot and
    wrong for a kicking one.** The lift used to project heel→toe onto
    the ground plane, which drags a foot pointed 40° up back to
    horizontal: the toe drops and is thrown forward into whatever the
    foot is aimed at. Grounded feet do not need it — the FK apply
    re-aims anything below 0.20 m along its own heading anyway.
32. **The landmark prefilter eats strikes too.** PITFALL #18 is about
    the spec's `smooth` windows; the same thing happens one stage
    earlier in the lift's default 7-frame Savitzky-Golay prefilter. On a
    roundhouse peaking over ~6 source frames it cost 5° of shin angle
    (0.04 m of foot height). `prefilter_window: 5` costs 1.4°.
33. **Capsule proxies are optimistic; only the MESHES decide.** The
    cheap collision model in `compare_pair.py` (a 0.16 m torso cylinder,
    a 0.11 m head sphere) runs outside Blender and is right about
    separation and reach. It is *not* the character's silhouette: a
    hood, a shoulder, a glove and a boot all live outside those
    surfaces. On the duel plate it scored a kick as clearing by 4 mm
    while the actual meshes intersected across 4 frames with up to 230
    overlapping face pairs — visible as a foot through a head. If a
    human says two characters touch and your numbers say otherwise,
    the numbers are measuring the wrong thing. Run
    `run_in_blender.py contact` (BVH overlap on the evaluated, skinned
    meshes) before believing any clearance.
34. **Check the WHOLE clip, not the beat you are working on.** Chasing
    the kick, the mesh check was run over frames 165–205 and reported
    it clean. Run over all 301 it found four more intersections nobody
    had looked at — including one at the closing T-pose, in a part of
    the clip that had been "finished" for two passes.
35. **A faithful retarget of two people can still be impossible.** Two
    Mixamo characters are not two humans: their limbs, heads and hands
    are thicker. The duel plate's roundhouse clears the defender's
    guard hand by 0.111 m centre-to-centre — with real forearms that is
    a 2 cm miss, and with these characters' meshes it is a collision.
    The retarget reproduced that 0.111 m to the centimetre and was
    *therefore* wrong on screen. When geometry and fidelity conflict,
    fidelity loses: the showcase has to read clean. Buy the clearance
    with the smallest measured departure, declare it in the spec, and
    let the comparator keep reporting it (it prints
    `[declared root_offset]`) so the cost stays visible instead of
    becoming folklore.
36. **Clearance must come from the stage, not from the poses.** Every
    pose-level lever was tried on this collision and each one moved the
    problem: raising the kick swept the foot through the guard hand;
    lowering the defender dropped his hands into the rising foot;
    curling his spine swung his arms forward into it; leaning him away
    lifted his head into the arc; tucking his chin did nothing because
    the contact was his shoulder. Only distance helped every frame at
    once. Requirements that flip between ADJACENT frames (dest 180
    wanted the foot lower, dest 181 wanted it higher) are the signature
    of a grazing contact that no single pose correction can satisfy.
37. **Ramp a stage offset through motion the character already has.**
    0.20 m of clearance applied while the support foot is planted is
    0.20 m of skate. On this plate the attacker already retreats 0.24 m
    between the punches and the kick, so the offset was ramped across
    exactly those frames (src 116–139) and out again while he resettles
    (162–185): nothing slides, and it reads as making range before a
    head kick — which is what a fighter does.
38. **Fist-into-glove is not a bug.** The punches in this plate land on
    the defender's block, and two rigid hands cannot deform, so the
    contact frames show deep face overlap (1016 pairs at the worst).
    That is the strike landing, exactly as in the video. Do not "fix"
    the frames where contact is the point — separate an intended
    contact from an unintended one by asking what the source video
    does at that frame.

## The SMPL-X path

39. **Positions cannot carry twist.** The landmark lift rebuilt the
    skeleton from 33 points and had to invent forearm roll, spine curve
    and head orientation. The estimator had already solved all three:
    transfer SMPL-X joint *rotations* (`retarget_smplx.py`), and align
    rest poses only where they differ in pose (limbs), never where the
    skeletons differ in proportion (spine, neck, head, collars).
40. **GVHMR's world trajectory drifts, in all three axes.** Measured on
    whole takes that end where they began: 0.3 m in depth (fight),
    0.4 m sideways (kung-fu), 5–15 cm vertically (feet "planted" 15 cm
    above the floor mid-clip). The camera-frame root is noisy but does
    not drift. Take height from the camera continuously; apply the
    horizontal correction per footfall, or it drags planted feet.
41. **The network's contact confidence can be confidently wrong.** On the
    kung-fu plate the right foot slides 0.2 m back under the body while
    `static_conf` stays near 1; a lock there left the character in a
    frog stance for the whole closing T-pose. On the spin plate it stays
    under 0.4 for 200 frames of footwork with the feet on the floor.
    Check every contact against foot speed and height, and add a
    geometric pass once the floor is known.
42. **A regressor flattens extremes.** GVHMR put the fight kick apex
    0.10–0.18 m below where ViTPose saw the ankle in the frames it was
    given. Refine the pose on the plate's own 2D keypoints
    (`refine_smplx.py`) instead of sizing `leg_pose` corrections by hand.
43. **Face keypoints are too small to steer a head.** At 4.7 m the face
    spans ~25 px: a 3 px detection error is a 10° head turn. Refining
    with full-weight face points rotated the head 17° on average; low
    weight plus a strong head/neck prior brought it to 2°.
44. **Linear resampling stutters.** Interpolating 24 fps samples linearly
    to 30 fps puts a velocity kink at every source frame, a jerk spike
    every fourth frame. Use a C1 spline on unrolled quaternions.
45. **Smoothing a faithful retarget costs amplitude.** The raw GVHMR
    rotations were not noisy; the legacy pre-filter was eating strikes
    (fist speed 3.5 m/s vs 5–6 m/s). Even a gentle One-Euro filter cost
    3–5 % of punch extension, so it is off by default.
46. **Rest blends must not touch the legs.** Blending the whole body to
    the rig's T-pose while the performer stands in a wide stance drags
    both planted feet across the floor at up to 0.8 m/s. Settle the upper
    body only.
47. **A flat foot is the rig's flat foot.** Aligning the foot bone to the
    SMPL-X ankle→ball direction carries SMPL-X foot geometry (a different
    pitch) and lifts the ball 5 mm off the floor. For a planted foot keep
    only the yaw of the alignment and lock that yaw for the whole stance,
    or the toes swing around the locked ankle.
48. **Joint centres are not keypoints.** A SMPL-X hip joint projects
    ~8 cm below the COCO hip keypoint. Fit and compare with the same
    definition (GVHMR's mesh-based COCO-17 regressor), and treat angle
    metrics between different definitions as relative.
49. **The ankle→ball joint line is not the foot.** On the SMPL-X template
    it reads 18–22° of toe-out where the heel-to-toes line of the mesh
    reads 5–6°. Aligning the rig's foot on the joint line turned every
    retargeted foot out by ~15°. Measure the foot on the mesh, align yaw
    only.
50. **A 2D fit un-plants feet.** Refining the pose frame by frame on
    detections moved feet GVHMR had pinned (planted-foot speed 0.3 →
    3.5 cm/s median, 12 cm of creep over 0.8 s on the kung-fu plate) —
    still in the image, sliding in the world. Re-pin the flagged feet
    after refining, anchored on the stillest one (an average lets a foot
    starting to lift drag the other).
51. **A camera correction averaged over a stance is not the footfall's.**
    During a 6 s deep stance the camera-frame depth drifted 0.2 m with the
    pose; averaging it moved a planted foot 12 cm at the moment the other
    foot lifted. Measure each footfall's correction when it lands.
52. **The image is the best contact detector.** The camera is static: a
    foot that does not move on screen is planted. GVHMR's flag missed
    such feet for dozens of frames on the kung-fu plate (where the 3D
    track crept), and the network flags some genuine slides as static.
    Plant a low foot that is still on screen, lock only while the planted
    point is still, and let a real slide follow the source.
53. **A static heel can be up.** The rear foot of a boxing stance is
    perfectly still on its ball. "Static" is not "flat": flat needs heel
    AND ball near the floor; otherwise pivot on the lower point and keep
    the real pitch.
54. **Check a model file before trusting it.** The Pose Landmarker model
    restored from shadow copy into `local/bible` starts with zeros — the
    same damage as the plates noted in the video-mocap docs. A `.task` is
    a zip: `detect_feet.py` checks for `PK` and downloads a fresh copy.
55. **A long heel plant is the estimator's, not the performer's.** GVHMR
    returns the planted rear foot of the kung-fu bow stance (4 s, leg
    straight) with its toes 20° up and rolled 37° onto its edge; the 2D
    refinement, pulled by a MediaPipe heel that lands mid-foot when the
    foot faces the camera, takes it to 29°. The video shows it flat. The
    image cannot arbitrate — a toe-up foot and a flat one turned 12°
    project within a pixel of each other — but weight-bearing can: heel
    strikes and turns on the heel last a fraction of a second, so a heel
    plant held longer is laid flat.
56. **A tilted foot's heading is not its axis's heading.** The heading of
    a foot rolled 37° onto its edge, read off its tilted forward axis,
    was 6° off the direction it points in once laid flat. Flatten first
    (minimal rotation to the floor), then read the heading.
57. **A planted foot's heading cannot be fitted to 2D keypoints.** Seen
    from the side, turning a foot moves its toes in depth: 1 cm of error
    in where the detector puts the ankle or the toe (MediaPipe's toe is
    the big toe tip, its heel sits 3-4 cm up the back of the heel) is
    ~15° of heading. Seen from the front it is the heights that dominate.
    Tried and dropped: a per-stance yaw fit to the ankle→toe line gave
    sub-degree residuals and moved feet 5-17° in both directions; which
    way depended on the landmark definition chosen. Keep the source's
    heading, and treat foot-angle metrics against MediaPipe as ±5°.
58. **Planted feet disagree with GVHMR's gravity — the feet, not the
    gravity.** The up vector of feet GVHMR flags as planted, and the plane
    through their soles, lean 2–10° from GVHMR's world up on all five
    plates. But a T-posed body stands within 1–2° of that same up, and the
    feet stand 0–12° toes-up with different values for the two feet of one
    take: it is the estimator's foot tilt (#64), not a tilted world. Do
    not re-level the world on the feet.
59. **The estimator slides the support foot.** Through the spin plate's
    kicks GVHMR moved the standing foot at up to 2 m/s and sank it 4–10 cm
    into the floor, while the video shows it fixed (the toe does not move
    on screen). Its contact flag stayed under 0.3. One foot 15 cm above
    the other means the lower one carries the body: plant it, and pin the
    body's translation to it (to its ball when the heel is up, so the body
    turns about it).
60. **Raising the body for one sinking foot floats the other.** Between
    contacts a free foot can go under the floor; lifting the hips to clear
    it left the other, correctly planted foot 8 cm in the air (spin,
    turning footwork). Lift the sinking foot with its own leg.
61. **A pivoting foot is down, not locked.** Counting a foot as planted
    when only its ball or heel is still on screen merged two kung-fu
    stances across a turn on the heel: one lock position for both, legs
    stretched, hips dropped to the cap, 0.3–0.7° worse leg angles, and no
    gain on the spin plate. Only whole-foot stillness may hold a lock.
62. **One heading cannot span a turn.** A flat lock keeps its median
    heading; a stance that pivots 90° on its heel barely moves the ankle,
    so the drift cut never fires. Cut the lock when the foot's heading
    moves 20° (uppercut: foot direction 11.5° → 7.8°).
63. **Fingers need a zoom.** On a full-body 1280×720 plate a hand is
    30–50 px: MediaPipe's palm detector, run on the frame, finds nothing
    usable. Cropped around the ViTPose wrist and upscaled to 256 px, the
    hand is found on 83–100 % of frames. The same zoom does nothing for
    the feet: MediaPipe's pose model looks at the whole body at a fixed
    256 px whatever the plate's resolution.
64. **GVHMR's feet carry a steady tilt.** Over the frames the network
    calls the whole foot static, its feet stand 0–12° toes-up and rolled a
    few degrees — steadily through a take, differently per foot. Flat locks
    hide it while planted; it shows as a flap at every lock ramp and a
    toes-up swing foot. Measure it on the static frames and take it off
    every frame: leg angles 0.1–0.4° better on four plates, foot direction
    better on all five (kung-fu 7.1° → 5.3°).
65. **Matching the performer's stance width breaks the legs.** The Y Bot's
    hip joints are 18 cm apart, the performers' 11–13 cm scaled, so
    hip-relative leg targets stand its feet ~3 cm wider each. Targets from
    the hip centre instead cost 1.5–2.3° on the leg angles of every plate
    and overshot in the overlay: the width is the rig's, keep it.
66. **The image places a planted foot sideways, not in depth.** The ray
    from the plate camera through the detected ankle meets the floor at
    12–19°; 1 cm of error in the ankle's height is 3–4 cm of depth. Moved
    across the line of sight only, planted feet sit exactly on the
    performer's in the overlay — and a rig with wider hips than the
    performer then angles its legs away from the video's (better on two
    plates, worse on three). Opt-in: `contact_image_anchor`.
67. **Do not pin the body to a foot nobody saw down.** Through the spin
    plate's turning footwork nothing says which foot is down, and the feet
    floated 3 cm. Planting the lower foot there — the legacy lift's rule —
    and pinning the body to it threw the overlay 106 px off the performer
    at frame 158 (the estimate lowers the wrong foot as the performer
    steps into the jump), and grounding the body on it every frame put the
    jump 10 px low. What works: set the hovering foot down with its own
    leg (height only), and let only OBSERVED contacts set the floor's
    height. A support the retarget infers (one foot well above the other)
    locks its foot; it does not move the body.

