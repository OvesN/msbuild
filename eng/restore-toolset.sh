#!/usr/bin/env bash

function InitializeCustomSDKToolset {
  if [[ "$restore" != true ]]; then
    return
  fi

  CreateBuildEnvScript
}

function CreateBuildEnvScript {
  mkdir -p $artifacts_dir
  scriptPath="$artifacts_dir/sdk-build-env.sh"
  ReadVersionFromJson 'dotnet'
  local bootstrap_sdk_version="$(sed -n 's/.*<BootstrapSdkVersion>\(.*\)<\/BootstrapSdkVersion>.*/\1/p' "$eng_root/Versions.props")"
  local dotnet_path="$artifacts_dir/bin/bootstrap/core"
  if [[ "$_ReadGlobalVersion" != "$bootstrap_sdk_version" ]]; then
    dotnet_path="$artifacts_dir/bin/bootstrap/build"
  fi
  scriptContents="
#!/usr/bin/env bash

export DOTNET_ROOT=$dotnet_path
export PATH=$dotnet_path:\$PATH
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0
"

  echo "$scriptContents" > ${scriptPath}
}

InitializeCustomSDKToolset
