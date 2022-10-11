#if TOOLS
using Godot;
using System;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;

[Tool]
public partial class UDotPlugin : EditorPlugin
{
	public override void _EnterTree()
	{
		AddToolMenuItem("Import UAsset", new Callable(() =>
		{
			var selectSourceDirDialog = new FileDialog();
			selectSourceDirDialog.FileMode = FileDialog.FileModeEnum.OpenDir;
			selectSourceDirDialog.Access = FileDialog.AccessEnum.Filesystem;
			GetEditorInterface().GetBaseControl().AddChild(selectSourceDirDialog);
			selectSourceDirDialog.PopupCentered(new Vector2i(300, 500));

			selectSourceDirDialog.DirSelected += async (string dir) =>
			{
				var selectOutDirDialog = new FileDialog();
				selectOutDirDialog.FileMode = FileDialog.FileModeEnum.OpenDir;
				selectOutDirDialog.Access = FileDialog.AccessEnum.Resources;
				GetEditorInterface().GetBaseControl().AddChild(selectOutDirDialog);
				selectOutDirDialog.PopupCentered(new Vector2i(300, 500));
				selectOutDirDialog.DirSelected += async (string outdir) =>
				{
					var umodel = "/home/ryhon/.local/bin/umodel";
					if (!System.IO.File.Exists(umodel)) return;

					var tmpdir = "/tmp/" + DateTime.Now.Ticks;
					System.IO.Directory.CreateDirectory(tmpdir);

					var files = System.IO.Directory.EnumerateFiles(dir, "*.uasset", System.IO.SearchOption.AllDirectories).ToList();
					var fcon = files.Count;
					var fprog = 0;

					var pop = new AcceptDialog();
					pop.OkButtonText = "Cancel";
					var cancel = false;
					pop.Confirmed += () => { cancel = true; };

					var flab = new Label();
					flab.Text = fprog + "/" + fcon;
					pop.AddChild(flab);

					GetEditorInterface().GetBaseControl().AddChild(pop);
					pop.PopupCentered(new Vector2i(300, 200));

					foreach (var f in files)
					{
						if (cancel) break;

						// GD.Print("[UDot] Extracting " + f);
						var psi = new ProcessStartInfo(umodel)
						{
							UseShellExecute = false,
							RedirectStandardOutput = true,
							ArgumentList =
						{
							"-export",
							"-sounds",
							"-png",
							"-gltf",
							"-path=" + dir,
							"-out="+tmpdir,
							f
						}
						};
						var p = new Process();
						p.StartInfo = psi;
						p.Start();

						await p.WaitForExitAsync();

						if (p.ExitCode != 0)
						{
							GD.PushError("[UDot] Exit code " + p.ExitCode + " when extracting " + f);
							if (false) break;
						}
						fprog++;
						flab.Text = fprog + "/" + fcon;
					}

					flab.Text = "Processing models...";

					var exfiles = System.IO.Directory.EnumerateFiles(tmpdir, "*", System.IO.SearchOption.AllDirectories).ToList();

					foreach (var gltfFile in exfiles.Where(f => f.EndsWith(".gltf")))
					{
						var jsonstr = await System.IO.File.ReadAllTextAsync(gltfFile);
						var json = JSON.ParseString(jsonstr).AsGodotDictionary();
						jsonstr = null;

						int addImage(string path)
						{
							if (!json.ContainsKey("images")) json["images"] = new Godot.Collections.Array();

							var dict = json["images"].AsGodotArray();
							var imgobj = new Godot.Collections.Dictionary();
							imgobj.Add("uri", path);
							dict.Add(imgobj);

							return dict.Count - 1;
						}

						int addTexture(int imgid)
						{
							if (!json.ContainsKey("textures")) json["textures"] = new Godot.Collections.Array();

							var dict = json["textures"].AsGodotArray();
							var imgobj = new Godot.Collections.Dictionary();
							imgobj.Add("source", imgid);
							dict.Add(imgobj);

							return dict.Count - 1;
						}

						if (!json.ContainsKey("materials")) continue;

						var mats = json["materials"].AsGodotArray();
						var flippedNormals = new List<string>();
						foreach (var mato in mats)
						{
							var mat = mato.AsGodotDictionary();
							if (!mat.ContainsKey("name")) continue;
							{
								var matFile = exfiles.FirstOrDefault(f => f.EndsWith("/" + (string)mat["name"] + ".mat"));
								if (matFile == null) continue;

								var matstr = await System.IO.File.ReadAllTextAsync(matFile);

								foreach (var matl in matstr.Split('\n'))
								{
									var eqpos = matl.IndexOf('=');
									if (eqpos == -1) continue;

									var fname = matl[0..eqpos];
									var val = matl[(eqpos + 1)..];

									if (fname == "Diffuse")
									{
										var diftex = exfiles.FirstOrDefault(f => f.EndsWith("/" + val + ".png"));
										if (!System.IO.File.Exists(diftex))
										{
											GD.PushError("[UDot] Diffuse texture defined but does not exist: " + diftex);
											continue;
										}

										var relTexFile = System.IO.Path.GetRelativePath(gltfFile, diftex)[3..]; // For whatever reason it's 1 level higher

										if (!mat.ContainsKey("pbrMetallicRoughness")) mat.Add("pbrMetallicRoughness", new Godot.Collections.Dictionary());
										var pbrobj = mat["pbrMetallicRoughness"].AsGodotDictionary();
										var baseColorObj = (pbrobj["baseColorTexture"] = new Godot.Collections.Dictionary()).AsGodotDictionary();
										baseColorObj["index"] = addTexture(addImage(relTexFile));
										pbrobj["baseColorFactor"] = new Godot.Collections.Array { 1f, 1f, 1f, 1f };
									}
									else if (fname == "Normal")
									{
										var normtex = exfiles.FirstOrDefault(f => f.EndsWith("/" + val + ".png"));
										if (!System.IO.File.Exists(normtex))
										{
											GD.PushError("[UDot] Normal texture defined but does not exist: " + normtex);
											continue;
										}

										if (!flippedNormals.Contains(normtex))
										{
											GD.Print("[UDot] Flipping normals for " + normtex);

											var convertExe = "/bin/convert";
											var npsi = new ProcessStartInfo(convertExe)
											{
												ArgumentList = 
												{
													normtex, "-channel", "G", "-negate", normtex
												},
												UseShellExecute = false,
												RedirectStandardOutput = true
											};
											var np = new Process();
											np.StartInfo = npsi;
											np.Start();

											await np.WaitForExitAsync();
											flippedNormals.Add(normtex);
										}

										var relTexFile = System.IO.Path.GetRelativePath(gltfFile, normtex)[3..];

										var normObj = new Godot.Collections.Dictionary();
										normObj.Add("index", addTexture(addImage(relTexFile)));
										// normObj.Add("texCoord", 0);
										mat.Add("normalTexture", normObj);
									}
								}
							}
						}

						await System.IO.File.WriteAllTextAsync(gltfFile, json.ToString());
					}

					pop.Hide();

					foreach (var exdir in System.IO.Directory.GetDirectories(tmpdir))
					{
						void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
						{
							// Get the subdirectories for the specified directory.
							var dir = new System.IO.DirectoryInfo(sourceDirName);
							var dirs = dir.GetDirectories();

							if (!dir.Exists)
							{
								throw new System.IO.DirectoryNotFoundException(
									"Source directory does not exist or could not be found: "
									+ sourceDirName);
							}

							// If the destination directory doesn't exist, create it. 
							if (!System.IO.Directory.Exists(destDirName))
							{
								System.IO.Directory.CreateDirectory(destDirName);
							}

							// Get the files in the directory and copy them to the new location.
							var files = dir.GetFiles();
							foreach (var file in files)
							{
								string temppath = System.IO.Path.Combine(destDirName, file.Name);
								file.CopyTo(temppath, true);
							}

							// If copying subdirectories, copy them and their contents to new location. 
							if (copySubDirs)
							{
								foreach (var subdir in dirs)
								{
									string temppath = System.IO.Path.Combine(destDirName, subdir.Name);
									DirectoryCopy(subdir.FullName, temppath, copySubDirs);
								}
							}
						}

						var to = ProjectSettings.GlobalizePath(outdir) + "/" + new System.IO.FileInfo(exdir).Name;
						GD.Print(exdir + " => " + to);
						DirectoryCopy(exdir, to, true);
					}
				};
			};
		}));
	}

	public override void _ExitTree()
	{
		RemoveToolMenuItem("Import UAsset");
	}
}
#endif
