using System.Collections.Generic;

namespace Jig
{
    // Wire format for a Jig. Deliberately dumb data: every field is optional-tolerant so
    // remote content can never hard-fail the viewer. Deserialized with Newtonsoft because
    // `visible` needs to distinguish "absent" (inherit) from "false" (hide), which
    // JsonUtility cannot express.

    public class JigManifest
    {
        public List<JigEntry> jigs = new List<JigEntry>();
    }

    public class JigEntry
    {
        public string id;
        public string title;
        public string scene;   // path relative to the manifest URL
    }

    public class JigScene
    {
        public string id;
        public string title;
        public string model;   // path relative to this scene.json
        public float scale = 1f;
        public List<JigStep> steps = new List<JigStep>();
    }

    public class JigStep
    {
        public string caption;
        public float duration = 0.8f;
        public List<JigNodeState> nodes = new List<JigNodeState>();
        public List<JigLabelSpec> labels = new List<JigLabelSpec>();
        public List<JigCalloutSpec> callouts = new List<JigCalloutSpec>();
    }

    // All transform fields are OFFSETS FROM THE NODE'S REST POSE as authored in the glTF,
    // not absolute local values. Nodes with a non-zero rest translation (groups, backplates)
    // would otherwise teleport when an author wrote a plausible-looking absolute position.
    // Omitted field => inherit the resolved value from the previous step.
    public class JigNodeState
    {
        public string path;      // slash-separated, relative to the model root: "Hands/Hand Seconds"
        public float[] move;     // [x,y,z] added to rest position, model-local units
        public float[] rotate;   // [x,y,z] euler degrees applied on top of rest rotation
        public float[] scale;    // [x,y,z] multiplied into rest scale
        public bool? visible;
    }

    public class JigLabelSpec
    {
        public string anchor;    // node path the leader line points at
        public string text;
        public float[] offset;   // label position relative to the anchor, model-local units
    }

    // A panel with a heading and a paragraph, for the things a label cannot say in three
    // words. Unlike a label, the anchor is OPTIONAL: a callout with no anchor floats beside
    // the model with no leader line, which is what a step-level aside needs.
    //
    // A scene written before callouts existed has no `callouts` key at all; Newtonsoft leaves
    // the field at its initialised empty list, so old content keeps working untouched.
    public class JigCalloutSpec
    {
        public string anchor;    // optional; absent or unresolvable means the callout floats
        public string title;
        public string body;
        public float[] offset;   // relative to the anchor, or to the model root when floating
        public float width = 6f; // wrap width in MODEL-LOCAL units, matching offset
    }
}
