# Prompting gen-video plates

The pipeline retargets *any* locked-camera footage, but its cheapest
source is AI video (the shipped plates came from Seedance and an
earlier image-to-video tool). This is how to prompt so the result
survives retargeting.

## The contract a plate must honor

1. **Camera locked**, full body in frame every frame, even light,
   1080p (720p minimum), 24–30 fps, ~10 s.
2. **T-pose held ~1 s at the START and the END**, square to camera.
   These bookends are the rest anchors (`rest_blend_start/_end`) and
   the proof the identity didn't drift. If the start pose is dirty the
   end T-pose alone is enough — but demand both anyway.
3. **Chest and hips face the camera** unless a turn IS the motion.
   Turns are retargetable; occlusion is not.
4. **Every moving limb stays visible.** Kicks travel just to the
   performer's side of the centerline so the leg never hides behind
   the torso. No props, tight kit, bare feet.
5. One continuous take, on the spot: no walking, no spinning, no
   extra strikes. Name the exact moves and forbid the rest.
6. Reuse the same first-frame still (image-to-video) across plates —
   same performer, same studio, same grid. Identity drift between
   plates breaks nothing technically but muddies comparison.

## Template

> Same man, same grey studio, same floor grid, camera locked, full body
> in frame the whole time. He starts holding this exact T-pose,
> perfectly still and facing the camera straight on, for a full second.
> Then he [MOVE 1 in one clause]. He then [MOVE 2]. Next he [MOVE 3,
> with visibility hints like "the kicking foot staying just to HIS
> right of the centerline so the whole leg stays visible"]. Finally he
> lowers his arms and spreads them straight out horizontally into a
> T-pose, feet returning under his shoulders, and holds that T-pose
> perfectly still, facing the camera, for the last full second. He
> stays on the same spot the whole time. His chest and hips face the
> camera the entire clip. No walking, no turning, no spinning, no
> extra strikes. Even lighting, bare feet, tight athletic kit, no
> props.

Write a must-see beat checklist next to the prompt (time window → what
must be visible) and regenerate the video if any beat fails — a hidden
foot cannot be retargeted, and regeneration is cheaper than a wasted
pass.

## Expect the model to disobey

Every plate so far deviated from its prompt somewhere: a left-knee jump
delivered as right-knee, an extended kick delivered as a knee snap, an
opening T-pose delivered as an A-pose. None of that matters IF you take
the beats and limbs from `analyze_landmarks.py` instead of from the
prompt (docs/PITFALLS.md #5–6). Prompt for what you want; retarget what
you got.

## Two performers in one plate

A fight between two characters is retargetable, but the plate has to
earn it. Everything above still applies, per performer, plus:

1. **Both square to the camera in the opening T-pose**, side by side,
   with a visible gap between their fingertips. The estimator makes
   frame 0's facing each character's forward, so a frontal bind puts
   both retargets in the render camera's frame and lets their turn
   toward each other come from the video itself — nothing authored. A
   profile T-pose also hides one whole arm, which is the one thing the
   pipeline cannot recover.
2. **They must never cross or swap sides of frame.** Performer identity
   is then the side of frame they occupy, which is a fact; tracker ids
   are not, and a swap splices half of each performer into one track.
3. **Their silhouettes must not overlap.** Contact reads best as a
   strike landing on a raised guard, or a clean miss. A clinch, a
   takedown or ground work will not survive.
4. **Different clothing colours.** Not decoration — it is how a human
   (and every still you review) tells the two tracks apart.
5. **Camera perpendicular to the fighting line**, with both fighters
   bladed but chests turned ~30° toward the lens. A side-on plate is
   actually *kinder* to the estimator than a frontal one: the action
   plane is perpendicular to the lens, so strike extension is measured
   rather than foreshortened.
6. **Frame for the tallest moment.** A head-high kick needs headroom
   the T-pose framing does not.

Travel is fine and wanted — closing distance is what makes an exchange
read (`root_motion` keeps it). Wandering around the floor is not.

A worked example, prompt and checklist, is the duel plate's SOURCE.md.
