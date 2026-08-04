# GPTino dev-loop paneling scene generator (RhinoPython / IronPython).
# Builds a paneling-friendly Rhino scene and saves it to $GPTINO_SCENE_3DM.
# Run via:  Rhino  /runscript="_-RunPythonScript ""scripts\dev-scene.py"" _-Exit"
# The output path is passed through the GPTINO_SCENE_3DM environment variable
# (RunPythonScript takes no CLI args). A '.scene-ok' marker is written on success.
import os
import rhinoscriptsyntax as rs

out = os.environ.get("GPTINO_SCENE_3DM")
if not out:
    raise Exception("GPTINO_SCENE_3DM is not set")

# Start from a clean document.
rs.Command("_-SelAll _Delete", False)

# A gently warped NURBS surface to panelize (10 m x 8 m, mm units), plus its
# boundary and a couple of freeform reveal curves and attractor points. This gives
# the agent selectable Rhino geometry (curves / surface / points) so the
# referenceRhinoObjects path (P0-1/P0-2/P0-3a) is exercised end to end.
corners = [(0, 0, 0), (10000, 0, 1500), (10000, 8000, 0), (0, 8000, 2500)]
rs.AddSrfPt(corners)

# Closed planar boundary rectangle (area ~ 80 m^2 -> exercises area/closed predicates).
rs.AddRectangle(rs.WorldXYPlane(), 10000, 8000)

# Two freeform facade reveal curves.
rs.AddCurve([(0, 2000, 0), (4000, 3000, 800), (10000, 2500, 300)])
rs.AddCurve([(0, 6000, 0), (5000, 5000, 1200), (10000, 6500, 200)])

# Attractor points.
rs.AddPoint(5000, 4000, 0)
rs.AddPoint(2000, 1000, 0)

# Purge fixture: a block definition with no instances placed. AddBlock consumes its input
# objects, so the definition exists and is unused -- the one thing purgeCandidates can report
# besides empty layers. It also gives the document a non-empty InstanceDefinitions table, which
# is what makes the layer census take its second (block-member) enumeration pass.
#
# The member is parked on its own layer 'BlockLib' on purpose: that layer has no top-level
# objects, so it is the fixture for the safety-critical claim that a layer holding only block
# geometry must never be reported as an empty leaf and offered for deletion.
rs.AddLayer("BlockLib")
_marker = rs.AddCircle(rs.WorldXYPlane(), 250)
rs.ObjectLayer(_marker, "BlockLib")
rs.AddBlock([_marker], (0, 0, 0), "GPTinoUnusedFixture", True)

# Scripted SaveAs (dash-prefixed = no dialog). Path has no spaces in the dev-loop tree.
rs.Command('_-SaveAs "%s" _Enter' % out, False)

marker = out + ".scene-ok"
with open(marker, "w") as handle:
    handle.write("scene generated\n")
