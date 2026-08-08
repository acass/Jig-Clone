using System;
using System.Collections.Generic;
using Jig;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// Self-check for JigStepResolver's inherit-if-omitted rule - the one piece of real logic in
/// the slice, and the one most likely to break silently. Run headlessly:
///
///   Unity -batchmode -nographics -projectPath . -executeMethod JigResolverCheck.Run -logFile -
///
/// Exits non-zero on failure so it works as a build gate.
public static class JigResolverCheck
{
    static int s_Failures;

    public static void Run()
    {
        s_Failures = 0;

        InheritsOmittedFields();
        LaterStepOverridesEarlier();
        EarlierStepsAreNotMutated();
        BadDataIsSkippedNotThrown();
        RealSceneFileParses();
        CalloutsAreOptionalAndTolerant();
        SceneIsWiredForInput();

        if (s_Failures > 0)
        {
            Debug.LogError($"[check] {s_Failures} failure(s)");
            EditorApplication.Exit(1);
        }

        Debug.Log("[check] all resolver checks passed");
        EditorApplication.Exit(0);
    }

    static void CalloutsAreOptionalAndTolerant()
    {
        // Content authored before callouts existed has no `callouts` key at all. It must
        // deserialize to an empty list, not null, or every pre-callout jig throws on the
        // first step change.
        var old = Parse(@"{
            ""steps"": [
                { ""caption"": ""before callouts"", ""nodes"": [], ""labels"": [] }
            ]
        }");
        Check(old.steps[0].callouts != null, "a scene with no 'callouts' key must give an empty list, not null");
        Check(old.steps[0].callouts.Count == 0, "an absent 'callouts' key must not invent entries");

        var scene = Parse(@"{
            ""steps"": [
                { ""callouts"": [
                    { ""title"": ""Anchored"", ""body"": ""Points at a part."", ""anchor"": ""A"", ""offset"": [1,2,3] },
                    { ""title"": ""Floating"", ""body"": ""No anchor at all."" },
                    { ""body"": ""Body only, no title."", ""width"": 4 }
                ] }
            ]
        }");

        var callouts = scene.steps[0].callouts;
        Check(callouts.Count == 3, "expected 3 callouts");
        Check(callouts[0].anchor == "A", "anchored callout lost its anchor");
        Check(callouts[0].offset != null && callouts[0].offset.Length == 3, "callout offset should survive parsing");
        Check(callouts[1].anchor == null, "a callout without an anchor must stay unanchored, not default to something");
        Check(callouts[2].title == null && callouts[2].body != null, "a body-only callout is legal");

        // width has a non-zero default, because a zero-width panel is invisible and the
        // author never wrote the field.
        Check(callouts[1].width > 0f, "callout width must default to something drawable");
        Check(Mathf.Approximately(callouts[2].width, 4f), "an authored callout width must win over the default");

        // Steps carry labels and callouts independently.
        Check(scene.steps[0].labels != null && scene.steps[0].labels.Count == 0,
              "a step with callouts but no labels should still give an empty label list");
    }

    static void InheritsOmittedFields()
    {
        // Step 1 moves the part; step 2 only rotates it. The move must persist.
        var scene = Parse(@"{
            ""steps"": [
                { ""nodes"": [] },
                { ""nodes"": [ { ""path"": ""A"", ""move"": [0,0,-3] } ] },
                { ""nodes"": [ { ""path"": ""A"", ""rotate"": [0,90,0] } ] }
            ]
        }");

        var r = JigStepResolver.Resolve(scene);
        Check(r.Count == 3, "expected 3 resolved steps");
        Check(r[0].Count == 0, "step 0 should touch nothing");
        Check(r[1]["A"].Move == new Vector3(0, 0, -3), "step 1 move not applied");
        Check(r[2]["A"].Move == new Vector3(0, 0, -3), "step 2 dropped the inherited move");
        Check(r[2]["A"].Rotate == new Vector3(0, 90, 0), "step 2 rotate not applied");
        Check(r[2]["A"].Scale == Vector3.one, "scale should default to one, not zero");
        Check(r[2]["A"].Visible, "visible should default to true");
    }

    static void LaterStepOverridesEarlier()
    {
        var scene = Parse(@"{
            ""steps"": [
                { ""nodes"": [ { ""path"": ""A"", ""move"": [1,0,0], ""visible"": true } ] },
                { ""nodes"": [ { ""path"": ""A"", ""move"": [0,0,0], ""visible"": false } ] }
            ]
        }");

        var r = JigStepResolver.Resolve(scene);
        Check(r[1]["A"].Move == Vector3.zero, "explicit zero move should override, not inherit");
        Check(!r[1]["A"].Visible, "explicit visible:false should override - this is why JsonUtility was not used");
    }

    static void EarlierStepsAreNotMutated()
    {
        var scene = Parse(@"{
            ""steps"": [
                { ""nodes"": [ { ""path"": ""A"", ""move"": [1,0,0] } ] },
                { ""nodes"": [ { ""path"": ""A"", ""move"": [2,0,0] } ] }
            ]
        }");

        var r = JigStepResolver.Resolve(scene);
        Check(r[0]["A"].Move == new Vector3(1, 0, 0),
            "step 0 was mutated by step 1 - snapshots are sharing a dictionary");
    }

    static void BadDataIsSkippedNotThrown()
    {
        var scene = Parse(@"{
            ""steps"": [
                { ""nodes"": [
                    { ""path"": ""A"", ""move"": [1,2] },
                    { ""move"": [1,2,3] },
                    { ""path"": ""B"", ""move"": [5,0,0] }
                ] }
            ]
        }");

        var warnings = new List<string>();
        List<Dictionary<string, NodeState>> r;
        try
        {
            r = JigStepResolver.Resolve(scene, warnings.Add);
        }
        catch (Exception e)
        {
            Check(false, $"malformed content threw instead of degrading: {e.Message}");
            return;
        }

        Check(warnings.Count == 2, $"expected 2 warnings, got {warnings.Count}");
        Check(r[0]["A"].Move == Vector3.zero, "2-element move should be ignored, leaving rest");
        Check(r[0]["B"].Move == new Vector3(5, 0, 0), "a bad sibling entry must not drop good ones");
    }

    static void RealSceneFileParses()
    {
        // The shipped content is part of the contract; a typo in it should fail the check.
        // Anchored to Assets/, not the working directory, so it resolves the same however
        // the editor was launched. Assets -> JigViewer -> unity -> JigClone.
        var path = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Application.dataPath, "../../../content/watch/scene.json"));
        if (!System.IO.File.Exists(path))
        {
            Check(false, $"content not found at {path}");
            return;
        }

        var scene = Parse(System.IO.File.ReadAllText(path));
        Check(scene.steps.Count == 6, $"expected 6 steps in shipped scene, got {scene.steps.Count}");
        Check(scene.scale > 0f, "shipped scene has a non-positive scale");

        var warnings = new List<string>();
        var r = JigStepResolver.Resolve(scene, warnings.Add);
        Check(warnings.Count == 0, $"shipped scene produced warnings: {string.Join("; ", warnings)}");

        // By the last step every part the teardown touches should still be displaced.
        var last = r[r.Count - 1];
        Check(last.ContainsKey("Glass Face") && last["Glass Face"].Move.z < 0f,
            "crystal should still be lifted at the final step");
        Check(last.ContainsKey("Backplate Khronos"),
            "final step should have moved the case back");
    }

    /// The scene shipped for months with no interactor in it, which made every button and the
    /// grab silently unpressable while everything still rendered correctly. That class of defect
    /// is invisible in the editor and expensive to find on a headset, so gate it here.
    static void SceneIsWiredForInput()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Jig.unity", OpenSceneMode.Single);

        Check(UnityEngine.Object.FindFirstObjectByType<XRRayInteractor>() != null,
            "no interactor in the scene - step buttons, grab and the picker are all unpressable");

        var placement = UnityEngine.Object.FindFirstObjectByType<JigPlacement>();
        Check(placement != null, "no JigPlacement in scene");
        if (placement == null) return;

        Check(placement.anchorManager != null,
            "JigPlacement.anchorManager unassigned - placement cannot anchor and will drift");
        Check(placement.rayInteractor != null,
            "JigPlacement.rayInteractor unassigned - re-placement cannot cast a ray");
        Check(placement.placeAction.action != null,
            "JigPlacement.placeAction unassigned - nothing triggers re-placement");
    }

    static JigScene Parse(string json) => JsonConvert.DeserializeObject<JigScene>(json);

    static void Check(bool condition, string message)
    {
        if (condition) return;
        Debug.LogError($"[check] FAIL: {message}");
        s_Failures++;
    }
}
