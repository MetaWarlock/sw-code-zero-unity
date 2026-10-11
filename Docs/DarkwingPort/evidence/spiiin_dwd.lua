 require("bit")
 
 Y_WEIGHT              = 0x550;
 Y_FORCE               = 0x560;
 
 ANIM_NO               = 0x420;
 ANIM_PARAM_FRAME_NO   = 0x410;
 ANIM_PARAM_TIMER      = 0x430;
 
 GRAVITY_CONST         = 0x40;
 
 LOC_NO                = 0x6D;
 
 INV_TIMER             = 0x7C;
 
 DWD_STATE_PARAM_50    = 0x50;
 DWD_PRED_STATE_50     = 0x58;
 DWD_STATE_PARAM_51    = 0x51;
 
 --
 SCENE_BOSS_MEGAVOLT   = 0x0C;
 SCENE_BOSS_LAST       = 0x08;
 
 MAX_INV_TIME          = 0x3C;
 
 DWD_STATE_50_HANG     = 0x4;
 DWD_STATE_50_HURT     = 0x6;
 DWD_STATE_50_DEATH    = 0x7;
 DWD_STATE_50_0C       = 0xC;
 DWD_STATE_50_0D       = 0xD;
 NoDeathAnimsStates = {DWD_STATE_50_HANG, DWD_STATE_50_0C, DWD_STATE_50_0D};
 
 ANIM_NO_HURT          = 0x09;
 ANIM_NO_1C            = 0x1C;
 ANIM_NO_1D            = 0x1D;
 ---
 function contains(e, t)
  for i = 1,#t do
    if t[i] == e then return true end
  end
  return false
end
 ---
 
 funcTable = {
   ["jumpMoveLeft"]  = 0xF254,
   ["jumpMoveRight"] = 0xF200,
 }

function writeDefaultGravity()
  x = memory.getregister("x")
  memory.writebyte(Y_WEIGHT+x, 0xAB);
  memory.writebyte(Y_FORCE+x, -1);
end;

function updateGravity()
  offset   = memory.getregister("x")
  force = memory.readbytesigned(Y_FORCE+offset);
  if (force > -8) then
    weightVal = memory.readbyte(Y_WEIGHT+offset);
    if (weightVal < GRAVITY_CONST) then
      memory.writebyte(Y_FORCE+offset, force-1);
    end;
    memory.writebyte(Y_WEIGHT+offset, weightVal-GRAVITY_CONST);
  else
    memory.writebyte(Y_FORCE+offset, -8);
    memory.writebyte(Y_WEIGHT+offset, 0);
  end;
end;

function writeAnimNoFromA()
  animNo = memory.getregister("a");
  writeAnimNo(animNo);
end;

function calcDeathAnimNoToA()
  memory.setregister("a", calcDeathAnimNo());
end;

---------------------------------------------------------------
function writeAnimNo(animNo)
  memory.writebyte(ANIM_NO, animNo);
  memory.writebyte(ANIM_PARAM_TIMER, 0);
  memory.writebyte(ANIM_PARAM_FRAME_NO, 0);
end;

function calcDeathAnimNo()
  locNumber = memory.readbyte(LOC_NO);
  x = memory.getregister("x");
  animNo = memory.readbyte(ANIM_NO+x);
  if ((locNumber == SCENE_BOSS_LAST) and (animNo == 0x4D)) then
    return ANIM_NO_1D;
  end;
  if ((locNumber == SCENE_BOSS_MEGAVOLT) and (animNo == 0x5D)) then
    return ANIM_NO_1C;
  end;
  return ANIM_NO_HURT;
end;

function rndShuffle(value)
  rndOffset = memory.readbytesigned(0xDA);
  dd = memory.readbytesigned(0xDC + rndOffset);
  if (dd == 0x88) then
    memory.writebyte(0xDC+rndOffset, value);
    memory.writebyte(0xDA, bit.band(rndOffset+1, 7));
  end;
end;

function rndShuffleFromA()
  rndShuffle(memory.getregister("a"));
end;

function writeDmgHearts()
  heartsAddresses  = {0x5B4, 0x5B8, 0x5B6, 0x5B2};
  heartEmptyValues = {0x82, 0x83, 0x83, 0x82};
  for  i=1,3 do 
    if (memory.readbyte(heartsAddresses[i]) ~= heartEmptyValues[i]) then
      memory.writebyte(heartsAddresses[i], heartEmptyValues[i]);
      return;
    end;
  end;
  --death case
  memory.writebyte(heartsAddresses[4], heartEmptyValues[4]);
  memory.writebyte(INV_TIMER, 0);
  memory.writebyte(DWD_STATE_PARAM_50, DWD_STATE_50_DEATH);
  memory.writebyte(0x4D0, 0x80); --write some restart param
  writeAnimNo(ANIM_NO_HURT);
end;

function addDmgParams()
  invTimer = memory.readbyte(INV_TIMER);
  if (invTimer == MAX_INV_TIME) then
    memory.writebyte(DWD_STATE_PARAM_51, 0x0F);
    state50 = memory.readbyte(DWD_STATE_PARAM_50);
    if (not contains(state50, NoDeathAnimsStates)) then
      writeAnimNo(calcDeathAnimNo());
    end;
    memory.writebyte(DWD_PRED_STATE_50, state50);
    memory.writebyte(DWD_STATE_PARAM_50, DWD_STATE_50_HURT);
  end;
  writeDmgHearts();
end;

function doLeftRightScrolls()
  -- change dwd position
  -- check collide state and write to byte 10
  -- something else that not dizassembe yet
  scrollState = memory.getregister("a");  -- 1 == left, 2 == right
  memory.writebyte(0x52, scrollState); --just temp write
  byte9e = memory.readbytesigned(0x9E); -- always 0?
  if (byte9e<0) then
    return false;
  end;
  if (bit.band(scrollState,1)==0) then
    memory.setregister("a", 3);
    memory.setregister("y", 2);
    emu.callfunction(funcTable["jumpMoveLeft"]);
  else
    if (memory.readbyte(0x6d) == 0x0E) then
      b400 = memory.readbyte(0x400);
      if (b400 == 0x50) or (b400==0x10) then
        return false;
      end;
    end;
    memory.setregister("a",0);
    memory.setregister("y",2);
    emu.callfunction(funcTable["jumpMoveRight"]);
  end;
  return true;
end;

function leftRightJumpMoving()
  if not doLeftRightScrolls() then
    return;
  end;
  COLLIDE_DAMAGE = 4;
  COLLIDE_ADDRESS = 0x10;
  dwdCollideState = memory.readbyte(COLLIDE_ADDRESS);
  invTimer = memory.readbyte(INV_TIMER);
  if ((dwdCollideState == COLLIDE_DAMAGE) and invTimer==0) then
    memory.writebyte(DWD_STATE_PARAM_50, DWD_STATE_50_HURT);
    memory.writebyte(DWD_STATE_PARAM_51, 0x0F);
    writeAnimNo(ANIM_NO_HURT);
    rndShuffle(0x15);
    memory.writebyte(invTimer, MAX_INV_TIME);
    addDmgParams();
  end;
end;

injected_table = {
  [1] = writeDefaultGravity,
  [2] = updateGravity,
  [3] = writeAnimNoFromA,
  [4] = rndShuffleFromA,
  [5] = calcDeathAnimNoToA,
  [6] = addDmgParams,
  [7] = leftRightJumpMoving,
};

function inject_handler(no)
  injected_table[no]();
end;